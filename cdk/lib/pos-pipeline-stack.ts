import * as path from 'path';
import * as fs from 'fs';
import { Duration, RemovalPolicy, Stack, StackProps, CfnOutput } from 'aws-cdk-lib';
import * as apigw from 'aws-cdk-lib/aws-apigateway';
import * as dynamodb from 'aws-cdk-lib/aws-dynamodb';
import * as lambda from 'aws-cdk-lib/aws-lambda';
import * as eventsources from 'aws-cdk-lib/aws-lambda-event-sources';
import * as logs from 'aws-cdk-lib/aws-logs';
import * as s3 from 'aws-cdk-lib/aws-s3';
import * as s3n from 'aws-cdk-lib/aws-s3-notifications';
import * as sqs from 'aws-cdk-lib/aws-sqs';
import { Construct } from 'constructs';

const PUBLISH_ROOT = path.join(__dirname, '..', '..', 'src', 'publish');
const EMPTY_BUNDLE = path.join(__dirname, '..', 'test', 'fixtures', 'empty-bundle');

/**
 * Resolves a published Lambda bundle, failing loudly if `build.sh` has not
 * been run. Without this check CDK would deploy an empty asset and the
 * failure would only surface at invoke time, as an opaque runtime error.
 *
 * POS_ALLOW_MISSING_BUNDLES lets the assertion tests run without the .NET SDK
 * installed: they assert on the synthesised template, which does not depend on
 * what is inside the bundle. It must never be set for a real deploy.
 */
function publishedCode(project: string): lambda.Code {
  const dir = path.join(PUBLISH_ROOT, project);

  if (fs.existsSync(dir)) {
    return lambda.Code.fromAsset(dir);
  }

  if (process.env.POS_ALLOW_MISSING_BUNDLES === '1') {
    return lambda.Code.fromAsset(EMPTY_BUNDLE);
  }

  throw new Error(
    `Missing ${dir}. Run ./build.sh from the repository root before cdk synth or deploy.`,
  );
}

export class PosPipelineStack extends Stack {
  constructor(scope: Construct, id: string, props?: StackProps) {
    super(scope, id, props);

    // ---- Storage -------------------------------------------------------

    const reportsBucket = new s3.Bucket(this, 'ReportsBucket', {
      blockPublicAccess: s3.BlockPublicAccess.BLOCK_ALL,
      encryption: s3.BucketEncryption.S3_MANAGED,
      enforceSSL: true,
      versioned: true,
      removalPolicy: RemovalPolicy.RETAIN,
    });

    const table = new dynamodb.Table(this, 'PositionsTable', {
      partitionKey: { name: 'flightId', type: dynamodb.AttributeType.STRING },
      sortKey: { name: 'reportedAt', type: dynamodb.AttributeType.STRING },
      billingMode: dynamodb.BillingMode.PAY_PER_REQUEST,
      encryption: dynamodb.TableEncryption.AWS_MANAGED,
      pointInTimeRecoverySpecification: { pointInTimeRecoveryEnabled: true },
      removalPolicy: RemovalPolicy.RETAIN,
    });

    // ---- Messaging -----------------------------------------------------

    const deadLetterQueue = new sqs.Queue(this, 'ParsedReportsDlq', {
      retentionPeriod: Duration.days(14),
      enforceSSL: true,
    });

    const parsedReportsQueue = new sqs.Queue(this, 'ParsedReportsQueue', {
      // Six times the calculator timeout: a queue's visibility timeout must
      // exceed its consumer's timeout, or a slow invocation gets a duplicate
      // delivery while it is still running.
      visibilityTimeout: Duration.seconds(180),
      retentionPeriod: Duration.days(4),
      enforceSSL: true,
      deadLetterQueue: { queue: deadLetterQueue, maxReceiveCount: 3 },
    });

    // ---- Lambdas -------------------------------------------------------

    const commonProps = {
      runtime: lambda.Runtime.DOTNET_8,
      memorySize: 512,
      timeout: Duration.seconds(30),
      logRetention: logs.RetentionDays.ONE_MONTH,
    };

    const ingestFn = new lambda.Function(this, 'IngestApiFunction', {
      ...commonProps,
      code: publishedCode('PosReportPipeline.IngestApi'),
      handler: 'PosReportPipeline.IngestApi::PosReportPipeline.IngestApi.Function::Handler',
      environment: { RAW_BUCKET: reportsBucket.bucketName },
    });

    const parserFn = new lambda.Function(this, 'ParserFunction', {
      ...commonProps,
      code: publishedCode('PosReportPipeline.Parser'),
      handler: 'PosReportPipeline.Parser::PosReportPipeline.Parser.Function::Handler',
      environment: {
        QUEUE_URL: parsedReportsQueue.queueUrl,
        QUARANTINE_PREFIX: 'quarantine/',
      },
    });

    const calculatorFn = new lambda.Function(this, 'CalculatorFunction', {
      ...commonProps,
      code: publishedCode('PosReportPipeline.Calculator'),
      handler: 'PosReportPipeline.Calculator::PosReportPipeline.Calculator.Function::Handler',
      environment: { TABLE_NAME: table.tableName },
    });

    const statusFn = new lambda.Function(this, 'StatusApiFunction', {
      ...commonProps,
      code: publishedCode('PosReportPipeline.StatusApi'),
      handler: 'PosReportPipeline.StatusApi::PosReportPipeline.StatusApi.Function::Handler',
      environment: { TABLE_NAME: table.tableName },
    });

    // ---- Wiring --------------------------------------------------------

    reportsBucket.grantPut(ingestFn, 'raw/*');

    // Only the raw/ prefix triggers the parser. Without the filter, the
    // parser's own quarantine copies would re-trigger it, forever.
    reportsBucket.addEventNotification(
      s3.EventType.OBJECT_CREATED,
      new s3n.LambdaDestination(parserFn),
      { prefix: 'raw/' },
    );

    reportsBucket.grantRead(parserFn, 'raw/*');
    reportsBucket.grantPut(parserFn, 'quarantine/*');
    parsedReportsQueue.grantSendMessages(parserFn);

    calculatorFn.addEventSource(
      new eventsources.SqsEventSource(parsedReportsQueue, {
        batchSize: 10,
        maxBatchingWindow: Duration.seconds(5),
        // Lets one poison message fail without re-driving its nine neighbours.
        reportBatchItemFailures: true,
      }),
    );

    table.grantWriteData(calculatorFn);
    table.grantReadData(statusFn);

    // ---- API -----------------------------------------------------------

    const api = new apigw.RestApi(this, 'PosReportApi', {
      restApiName: 'POS Report Pipeline',
      description: 'Ingest aircraft position reports and query flight status.',
      deployOptions: {
        stageName: 'prod',
        loggingLevel: apigw.MethodLoggingLevel.INFO,
        metricsEnabled: true,
      },
    });

    api.root
      .addResource('pos-reports')
      .addMethod('POST', new apigw.LambdaIntegration(ingestFn));

    api.root
      .addResource('status')
      .addResource('{flightId}')
      .addMethod('GET', new apigw.LambdaIntegration(statusFn));

    // ---- Outputs -------------------------------------------------------

    new CfnOutput(this, 'ApiUrl', { value: api.url });
    new CfnOutput(this, 'ReportsBucketName', { value: reportsBucket.bucketName });
    new CfnOutput(this, 'PositionsTableName', { value: table.tableName });
    new CfnOutput(this, 'ParsedReportsQueueUrl', { value: parsedReportsQueue.queueUrl });
    new CfnOutput(this, 'DeadLetterQueueUrl', { value: deadLetterQueue.queueUrl });
  }
}
