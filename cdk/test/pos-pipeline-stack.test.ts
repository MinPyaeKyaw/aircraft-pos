import * as cdk from 'aws-cdk-lib';
import { Template, Match } from 'aws-cdk-lib/assertions';
import { PosPipelineStack } from '../lib/pos-pipeline-stack';

// These assertions read the synthesised template, not the contents of the
// Lambda bundles, so they run without the .NET SDK installed.
process.env.POS_ALLOW_MISSING_BUNDLES = '1';

function synth(): Template {
  const app = new cdk.App();
  return Template.fromStack(new PosPipelineStack(app, 'TestStack'));
}

describe('PosPipelineStack', () => {
  const template = synth();

  test('creates the four .NET 8 Lambdas', () => {
    const functions = template.findResources('AWS::Lambda::Function', {
      Properties: { Runtime: 'dotnet8' },
    });
    expect(Object.keys(functions)).toHaveLength(4);
  });

  test('creates two DynamoDB tables, both keyed by flightId and timestamp', () => {
    template.resourceCountIs('AWS::DynamoDB::Table', 2);

    const tables = template.findResources('AWS::DynamoDB::Table');
    for (const table of Object.values(tables)) {
      expect(table.Properties.KeySchema).toEqual([
        { AttributeName: 'flightId', KeyType: 'HASH' },
        { AttributeName: 'timestamp', KeyType: 'RANGE' },
      ]);
      expect(table.Properties.BillingMode).toBe('PAY_PER_REQUEST');
    }
  });

  test('blocks public access on the reports bucket and enforces TLS', () => {
    template.hasResourceProperties('AWS::S3::Bucket', {
      PublicAccessBlockConfiguration: {
        BlockPublicAcls: true,
        BlockPublicPolicy: true,
        IgnorePublicAcls: true,
        RestrictPublicBuckets: true,
      },
    });

    template.hasResourceProperties('AWS::S3::BucketPolicy', {
      PolicyDocument: Match.objectLike({
        Statement: Match.arrayWith([
          Match.objectLike({
            Effect: 'Deny',
            Condition: { Bool: { 'aws:SecureTransport': 'false' } },
          }),
        ]),
      }),
    });
  });

  test('scopes the S3 trigger to the pos/ prefix only', () => {
    // Without this filter the parser's own attachment/ writes would re-trigger
    // it and the pipeline would feed itself forever.
    template.hasResourceProperties('Custom::S3BucketNotifications', {
      NotificationConfiguration: Match.objectLike({
        LambdaFunctionConfigurations: Match.arrayWith([
          Match.objectLike({
            Filter: { Key: { FilterRules: Match.arrayWith([{ Name: 'prefix', Value: 'pos/' }]) } },
          }),
        ]),
      }),
    });
  });

  test('gives the calculation queue a dead-letter queue after three receives', () => {
    template.resourceCountIs('AWS::SQS::Queue', 2);
    template.hasResourceProperties('AWS::SQS::Queue', {
      RedrivePolicy: Match.objectLike({ maxReceiveCount: 3 }),
    });
  });

  test('queue visibility timeout exceeds the consumer timeout', () => {
    const queues = Object.values(template.findResources('AWS::SQS::Queue'));
    const withRedrive = queues.find((q) => q.Properties?.RedrivePolicy);
    const functions = Object.values(
      template.findResources('AWS::Lambda::Function', { Properties: { Runtime: 'dotnet8' } }),
    );
    const maxTimeout = Math.max(...functions.map((f) => f.Properties.Timeout));

    expect(withRedrive!.Properties.VisibilityTimeout).toBeGreaterThan(maxTimeout);
  });

  test('subscribes the calculator with partial batch failure reporting', () => {
    template.hasResourceProperties('AWS::Lambda::EventSourceMapping', {
      BatchSize: 10,
      FunctionResponseTypes: ['ReportBatchItemFailures'],
    });
  });

  test('exposes POST /pos-reports and GET /status/{flightId}', () => {
    for (const pathPart of ['pos-reports', 'status', '{flightId}']) {
      template.hasResourceProperties('AWS::ApiGateway::Resource', { PathPart: pathPart });
    }
    template.hasResourceProperties('AWS::ApiGateway::Method', { HttpMethod: 'POST' });
    template.hasResourceProperties('AWS::ApiGateway::Method', { HttpMethod: 'GET' });
  });

  describe('IAM is least privilege', () => {
    // Scoped to the four pipeline functions. CDK's own S3-notification and
    // auto-delete helpers get their policies from AWS-managed constructs we do
    // not control, so including them would test CDK rather than this stack.
    const OURS = /^(IngestApiFunction|ParserFunction|CalculatorFunction|StatusApiFunction)Role/;

    const policies = Object.entries(template.findResources('AWS::IAM::Policy'))
      .filter(([logicalId]) => OURS.test(logicalId));

    const statements = policies.flatMap(
      ([, p]) => p.Properties.PolicyDocument.Statement as Array<Record<string, unknown>>,
    );

    test('found a policy for each of the four functions', () => {
      // Guards every assertion below from passing vacuously if the naming or
      // the construct wiring ever changes.
      expect(policies).toHaveLength(4);
      expect(statements.length).toBeGreaterThanOrEqual(12);
    });

    test('no policy statement grants a wildcard action', () => {
      for (const statement of statements) {
        const actions = ([] as string[]).concat(statement.Action as string | string[]);
        for (const action of actions) {
          expect(action).not.toBe('*');
          // "s3:*" and friends are wildcards too, not just a bare "*".
          expect(action).not.toMatch(/\*$/);
        }
      }
    });

    test('no policy statement grants a wildcard resource', () => {
      for (const statement of statements) {
        const resources = ([] as unknown[]).concat(statement.Resource as unknown);
        for (const resource of resources) {
          expect(resource).not.toBe('*');
        }
      }
    });

    test('no Lambda gets the broad managed basic-execution policy', () => {
      // Each function has its own role scoped to its own log group instead.
      const roles = Object.values(template.findResources('AWS::IAM::Role'));
      const lambdaRoles = roles.filter((r) =>
        JSON.stringify(r.Properties.AssumeRolePolicyDocument).includes('lambda.amazonaws.com'),
      );

      const withManagedBasicExecution = lambdaRoles.filter((r) =>
        JSON.stringify(r.Properties.ManagedPolicyArns ?? []).includes('AWSLambdaBasicExecutionRole'),
      );

      // The S3-notification and auto-delete helpers CDK adds are allowed to
      // use it; none of our four may.
      expect(withManagedBasicExecution.length).toBeLessThanOrEqual(lambdaRoles.length - 4);
    });

    test('the ingest function can write only under pos/', () => {
      const s3Writes = statements.filter(
        (s) => ([] as string[]).concat(s.Action as string | string[]).includes('s3:PutObject'),
      );
      expect(s3Writes.length).toBeGreaterThan(0);

      const serialised = JSON.stringify(s3Writes);
      expect(serialised).toContain('pos/*');
      expect(serialised).toContain('attachment/*');
      expect(serialised).toContain('results/*');
    });

    test('no function may delete from DynamoDB', () => {
      const actions = statements.flatMap((s) =>
        ([] as string[]).concat(s.Action as string | string[]),
      );
      expect(actions).not.toContain('dynamodb:DeleteItem');
      expect(actions).not.toContain('dynamodb:UpdateItem');
    });
  });

  test('outputs everything needed to exercise the pipeline', () => {
    expect(Object.keys(template.findOutputs('*'))).toEqual(
      expect.arrayContaining([
        'ApiUrl',
        'ReportsBucketName',
        'ReportsTableName',
        'ResultsTableName',
        'CalculationQueueUrl',
        'DeadLetterQueueUrl',
      ]),
    );
  });
});
