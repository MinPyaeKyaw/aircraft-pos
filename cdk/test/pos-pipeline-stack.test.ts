import * as cdk from 'aws-cdk-lib';
import { Template, Match } from 'aws-cdk-lib/assertions';
import { PosPipelineStack } from '../lib/pos-pipeline-stack';

// The assertions below read the synthesised template, not the contents of the
// Lambda bundles, so they can run without the .NET SDK installed.
process.env.POS_ALLOW_MISSING_BUNDLES = '1';

function synth(): Template {
  const app = new cdk.App();
  const stack = new PosPipelineStack(app, 'TestStack');
  return Template.fromStack(stack);
}

describe('PosPipelineStack', () => {
  const template = synth();

  test('creates exactly four lambda functions on the dotnet8 runtime', () => {
    // The log-retention custom resource adds its own function, so filter by
    // runtime rather than counting every AWS::Lambda::Function.
    const functions = template.findResources('AWS::Lambda::Function', {
      Properties: { Runtime: 'dotnet8' },
    });
    expect(Object.keys(functions)).toHaveLength(4);
  });

  test('creates the reports bucket with public access blocked and versioning on', () => {
    template.hasResourceProperties('AWS::S3::Bucket', {
      VersioningConfiguration: { Status: 'Enabled' },
      PublicAccessBlockConfiguration: {
        BlockPublicAcls: true,
        BlockPublicPolicy: true,
        IgnorePublicAcls: true,
        RestrictPublicBuckets: true,
      },
    });
  });

  test('creates the positions table keyed by flightId and reportedAt', () => {
    template.hasResourceProperties('AWS::DynamoDB::Table', {
      KeySchema: [
        { AttributeName: 'flightId', KeyType: 'HASH' },
        { AttributeName: 'reportedAt', KeyType: 'RANGE' },
      ],
      BillingMode: 'PAY_PER_REQUEST',
    });
  });

  test('gives the parsed-reports queue a redrive policy of three receives', () => {
    template.hasResourceProperties('AWS::SQS::Queue', {
      RedrivePolicy: Match.objectLike({ maxReceiveCount: 3 }),
    });
  });

  test('creates both the main queue and its dead-letter queue', () => {
    template.resourceCountIs('AWS::SQS::Queue', 2);
  });

  test('triggers the parser only on the raw/ prefix', () => {
    // The notification goes through a custom resource, so assert on the
    // synthesised filter rule rather than a native bucket property.
    template.hasResourceProperties('Custom::S3BucketNotifications', {
      NotificationConfiguration: Match.objectLike({
        LambdaFunctionConfigurations: Match.arrayWith([
          Match.objectLike({
            Filter: {
              Key: {
                FilterRules: Match.arrayWith([{ Name: 'prefix', Value: 'raw/' }]),
              },
            },
          }),
        ]),
      }),
    });
  });

  test('subscribes the calculator to the queue with partial batch failures enabled', () => {
    template.hasResourceProperties('AWS::Lambda::EventSourceMapping', {
      BatchSize: 10,
      FunctionResponseTypes: ['ReportBatchItemFailures'],
    });
  });

  test('exposes POST /pos-reports and GET /status/{flightId}', () => {
    template.hasResourceProperties('AWS::ApiGateway::Resource', { PathPart: 'pos-reports' });
    template.hasResourceProperties('AWS::ApiGateway::Resource', { PathPart: 'status' });
    template.hasResourceProperties('AWS::ApiGateway::Resource', { PathPart: '{flightId}' });

    template.hasResourceProperties('AWS::ApiGateway::Method', { HttpMethod: 'POST' });
    template.hasResourceProperties('AWS::ApiGateway::Method', { HttpMethod: 'GET' });
  });

  test('outputs the API url and resource names', () => {
    const outputs = template.findOutputs('*');
    expect(Object.keys(outputs)).toEqual(
      expect.arrayContaining([
        'ApiUrl',
        'ReportsBucketName',
        'PositionsTableName',
        'ParsedReportsQueueUrl',
        'DeadLetterQueueUrl',
      ]),
    );
  });
});
