using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using System.Net;

namespace TestFramework.Container.Azure.FunctionApp;

public sealed class LocalFunctionAppSmokeFunction
{
    [Function("SmokeHttp")]
    public IActionResult Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequest request)
    {
        return new ObjectResult("Local smoke function executed.")
        {
            StatusCode = StatusCodes.Status500InternalServerError,
        };
    }
}