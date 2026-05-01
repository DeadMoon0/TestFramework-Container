using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using System.Net;

namespace TestFramework.Container.Azure.FunctionApp;

public sealed class LocalFunctionAppSmokeFunction
{
    [Function(nameof(Run))]
    public HttpResponseData Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequestData request)
    {
        HttpResponseData response = request.CreateResponse(HttpStatusCode.InternalServerError);
        response.WriteString("Local smoke function executed.");
        return response;
    }
}