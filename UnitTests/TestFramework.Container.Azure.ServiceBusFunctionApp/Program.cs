using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);
var configuration = builder.Configuration;

builder.Services.AddSingleton(_ => new ServiceBusClient(GetRequiredSetting("ServiceBusReplyConnectionString")));

builder.Build().Run();

string GetRequiredSetting(string key)
{
	return configuration[key] ?? throw new InvalidOperationException($"The required Function App setting '{key}' was not configured.");
}