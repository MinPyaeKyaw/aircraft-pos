import * as path from 'path';
import * as fs from 'fs';
import { CfnOutput, Duration, RemovalPolicy, Stack, StackProps } from 'aws-cdk-lib';
import * as apigw from 'aws-cdk-lib/aws-apigateway';
import * as dynamodb from 'aws-cdk-lib/aws-dynamodb';
import * as iam from 'aws-cdk-lib/aws-iam';
import * as lambda from 'aws-cdk-lib/aws-lambda';
import * as eventsources from 'aws-cdk-lib/aws-lambda-event-sources';
import * as logs from 'aws-cdk-lib/aws-logs';
import * as s3 from 'aws-cdk-lib/aws-s3';
import * as s3n from 'aws-cdk-lib/aws-s3-notifications';
import * as sqs from 'aws-cdk-lib/aws-sqs';
import { Construct } from 'constructs';

const PUBLISH_ROOT = path.join(__dirname, '..', '..', 'src', 'publish');
const EMPTY_BUNDLE = path.join(__dirname, '..', 'test', 'fixtures', 'empty-bundle');

/** S3 key prefixes. These must match PosObjectKeys in the C# shared library. */
const RAW_PREFIX = 'pos/';
const ATTACHMENT_PREFIX = 'attachment/';
const RESULTS_PREFIX = 'results/';

/**
 * Resolves a published Lambda bundle, failing loudly if ./build.sh has not been
 * run. Without this check CDK would upload an empty asset and the problem would
 * only surface as an opaque runtime error on the first invocation.
 *
 * POS_ALLOW_MISSING_BUNDLES exists so the assertion tests can run without the
 * .NET SDK installed -- they read the synthesised template, which does not
 * depend on bundle contents. It must never be set for a real deploy.
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
    `Missing ${dir}. Run ./build.sh from the repository root before cdk synth or cdk deploy.`,
  );
}

export class PosPipelineStack extends Stack {
  constructor(scope: Construct, id: string, props?: StackProps) {
    super(scope, id, props);

    // Deploy/destroy convenience is the default because this is an assessment
    // pipeline a reviewer will tear down. Deploy with `-c retainData=true` to
    // keep the bucket and tables instead, which is what production wants.
    const retainData = this.node.tryGetContext('retainData') === true;
    const dataRemoval = retainData ? RemovalPolicy.RETAIN : RemovalPolicy.DESTROY;

    // ---- Storage ---------------------------------------------------------

    const reportsBucket = new s3.Bucket(this, 'ReportsBucket', {
      blockPublicAccess: s3.BlockPublicAccess.BLOCK_ALL,
      encryption: s3.BucketEncryption.S3_MANAGED,
      enforceSSL: true,
      versioned: true,
      removalPolicy: dataRemoval,
      autoDeleteObjects: !retainData,
    });

    // Two tables, per the brief: the parser's index of reports, and the
    // calculator's index of results. Keeping them apart means a change to how
    // results are written can never corrupt the record of what was received.
    const reportsTable = new dynamodb.Table(this, 'ReportsTable', {
      partitionKey: { name: 'flightId', type: dynamodb.AttributeType.STRING },
      sortKey: { name: 'timestamp', type: dynamodb.AttributeType.STRING },
      billingMode: dynamodb.BillingMode.PAY_PER_REQUEST,
      encryption: dynamodb.TableEncryption.AWS_MANAGED,
      pointInTimeRecoverySpecification: { pointInTimeRecoveryEnabled: true },
      removalPolicy: dataRemoval,
    });

    const resultsTable = new dynamodb.Table(this, 'ResultsTable', {
      partitionKey: { name: 'flightId', type: dynamodb.AttributeType.STRING },
      sortKey: { name: 'timestamp', type: dynamodb.AttributeType.STRING },
      billingMode: dynamodb.BillingMode.PAY_PER_REQUEST,
      encryption: dynamodb.TableEncryption.AWS_MANAGED,
      pointInTimeRecoverySpecification: { pointInTimeRecoveryEnabled: true },
      removalPolicy: dataRemoval,
    });

    // ---- Messaging -------------------------------------------------------

    const deadLetterQueue = new sqs.Queue(this, 'CalculationDlq', {
      retentionPeriod: Duration.days(14),
      enforceSSL: true,
    });

    const calculationQueue = new sqs.Queue(this, 'CalculationQueue', {
      // Six times the consumer's timeout. A visibility timeout shorter than the
      // consumer's timeout hands the same message to a second invocation while
      // the first is still running.
      visibilityTimeout: Duration.seconds(180),
      retentionPeriod: Duration.days(4),
      enforceSSL: true,
      deadLetterQueue: { queue: deadLetterQueue, maxReceiveCount: 3 },
    });

    // ---- Lambdas ---------------------------------------------------------

    const ingestFn = this.createFunction('IngestApiFunction', 'PosReportPipeline.IngestApi', {
      REPORTS_BUCKET: reportsBucket.bucketName,
    });

    const parserFn = this.createFunction('ParserFunction', 'PosReportPipeline.Parser', {
      REPORTS_BUCKET: reportsBucket.bucketName,
      REPORTS_TABLE: reportsTable.tableName,
      QUEUE_URL: calculationQueue.queueUrl,
    });

    const calculatorFn = this.createFunction('CalculatorFunction', 'PosReportPipeline.Calculator', {
      REPORTS_BUCKET: reportsBucket.bucketName,
      REPORTS_TABLE: reportsTable.tableName,
      RESULTS_TABLE: resultsTable.tableName,
    });

    const statusFn = this.createFunction('StatusApiFunction', 'PosReportPipeline.StatusApi', {
      REPORTS_BUCKET: reportsBucket.bucketName,
      REPORTS_TABLE: reportsTable.tableName,
      RESULTS_TABLE: resultsTable.tableName,
    });

    // ---- IAM -------------------------------------------------------------
    //
    // Written as explicit statements rather than CDK's grant* helpers. The
    // helpers are convenient but generous: grantWrite on a table also allows
    // UpdateItem, DeleteItem and BatchWriteItem, and grantRead on a bucket adds
    // ListBucket and GetBucket*. Each function below gets exactly the actions
    // it calls, on exactly the prefixes it touches. No action is a wildcard.

    ingestFn.addToRolePolicy(new iam.PolicyStatement({
      actions: ['s3:PutObject'],
      resources: [reportsBucket.arnForObjects(`${RAW_PREFIX}*`)],
    }));

    parserFn.addToRolePolicy(new iam.PolicyStatement({
      actions: ['s3:GetObject'],
      resources: [reportsBucket.arnForObjects(`${RAW_PREFIX}*`)],
    }));
    parserFn.addToRolePolicy(new iam.PolicyStatement({
      actions: ['s3:PutObject'],
      resources: [reportsBucket.arnForObjects(`${ATTACHMENT_PREFIX}*`)],
    }));
    parserFn.addToRolePolicy(new iam.PolicyStatement({
      actions: ['dynamodb:PutItem'],
      resources: [reportsTable.tableArn],
    }));
    parserFn.addToRolePolicy(new iam.PolicyStatement({
      actions: ['sqs:SendMessage'],
      resources: [calculationQueue.queueArn],
    }));

    calculatorFn.addToRolePolicy(new iam.PolicyStatement({
      actions: ['dynamodb:GetItem'],
      resources: [reportsTable.tableArn],
    }));
    calculatorFn.addToRolePolicy(new iam.PolicyStatement({
      actions: ['s3:GetObject'],
      resources: [reportsBucket.arnForObjects(`${ATTACHMENT_PREFIX}*`)],
    }));
    calculatorFn.addToRolePolicy(new iam.PolicyStatement({
      actions: ['s3:PutObject'],
      resources: [reportsBucket.arnForObjects(`${RESULTS_PREFIX}*`)],
    }));
    calculatorFn.addToRolePolicy(new iam.PolicyStatement({
      actions: ['dynamodb:PutItem'],
      resources: [resultsTable.tableArn],
    }));

    statusFn.addToRolePolicy(new iam.PolicyStatement({
      actions: ['dynamodb:Query'],
      resources: [reportsTable.tableArn, resultsTable.tableArn],
    }));
    statusFn.addToRolePolicy(new iam.PolicyStatement({
      actions: ['s3:GetObject'],
      resources: [reportsBucket.arnForObjects(`${RESULTS_PREFIX}*`)],
    }));

    // ---- Wiring ----------------------------------------------------------

    // Scoped to the raw prefix only. Without the filter the parser's own
    // attachment/ and the calculator's results/ writes would re-trigger it,
    // and the pipeline would feed itself forever.
    reportsBucket.addEventNotification(
      s3.EventType.OBJECT_CREATED,
      new s3n.LambdaDestination(parserFn),
      { prefix: RAW_PREFIX },
    );

    calculatorFn.addEventSource(new eventsources.SqsEventSource(calculationQueue, {
      batchSize: 10,
      maxBatchingWindow: Duration.seconds(5),
      // One poison message fails on its own instead of re-driving the other
      // nine alongside it.
      reportBatchItemFailures: true,
    }));

    // ---- API -------------------------------------------------------------

    const api = new apigw.RestApi(this, 'PosReportApi', {
      restApiName: 'POS Report Pipeline',
      description: 'Receive aircraft position reports and query flight status.',
      deployOptions: {
        stageName: 'prod',
        loggingLevel: apigw.MethodLoggingLevel.ERROR,
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

    // ---- Outputs ---------------------------------------------------------

    new CfnOutput(this, 'ApiUrl', { value: api.url });
    new CfnOutput(this, 'ReportsBucketName', { value: reportsBucket.bucketName });
    new CfnOutput(this, 'ReportsTableName', { value: reportsTable.tableName });
    new CfnOutput(this, 'ResultsTableName', { value: resultsTable.tableName });
    new CfnOutput(this, 'CalculationQueueUrl', { value: calculationQueue.queueUrl });
    new CfnOutput(this, 'DeadLetterQueueUrl', { value: deadLetterQueue.queueUrl });
  }

  /**
   * One .NET 8 Lambda with its own log group and its own role.
   *
   * The role is built here rather than left to CDK so that log access is scoped
   * to this function's log group. The AWSLambdaBasicExecutionRole managed policy
   * that CDK would otherwise attach allows writing to every log group in the
   * account.
   */
  private createFunction(
    id: string,
    project: string,
    environment: Record<string, string>,
  ): lambda.Function {
    const logGroup = new logs.LogGroup(this, `${id}Logs`, {
      retention: logs.RetentionDays.ONE_MONTH,
      removalPolicy: RemovalPolicy.DESTROY,
    });

    const role = new iam.Role(this, `${id}Role`, {
      assumedBy: new iam.ServicePrincipal('lambda.amazonaws.com'),
      description: `Execution role for ${id}`,
    });

    logGroup.grantWrite(role);

    return new lambda.Function(this, id, {
      runtime: lambda.Runtime.DOTNET_8,
      code: publishedCode(project),
      handler: `${project}::${project}.Function::Handler`,
      // .NET cold starts are slow at the 128 MB floor, and CPU is allocated in
      // proportion to memory, so 512 MB is usually cheaper per invocation as
      // well as faster.
      memorySize: 512,
      timeout: Duration.seconds(30),
      environment,
      role,
      logGroup,
    });
  }
}
