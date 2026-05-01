using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;

namespace TestFramework.Container.Azure.ServiceBusFunctionApp;

public sealed class LocalServiceBusFunctionAppSmokeFunction(
    ServiceBusClient serviceBusClient,
    IConfiguration configuration)
{
    [Function(nameof(Run))]
    public async Task Run(
        [ServiceBusTrigger("%ServiceBusTriggerTopicName%", "%ServiceBusTriggerSubscriptionName%", Connection = "ServiceBusTriggerConnection")] string body,
        CancellationToken cancellationToken)
    {
        string replyQueueName = configuration["ServiceBusReplyTopicName"]
            ?? throw new InvalidOperationException("The required Function App setting 'ServiceBusReplyTopicName' was not configured.");

        await using ServiceBusSender sender = serviceBusClient.CreateSender(replyQueueName);
        await sender.SendMessageAsync(new ServiceBusMessage($"processed:{body}")
        {
            CorrelationId = body,
            Subject = "servicebus-smoke-processed",
        }, cancellationToken).ConfigureAwait(false);
    }
}