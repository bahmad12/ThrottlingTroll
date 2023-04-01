using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ThrottlingTroll;

var builder = new HostBuilder();

// Need to explicitly load configuration from host.json
builder.ConfigureAppConfiguration(configBuilder => {

    configBuilder.AddJsonFile("host.json", optional: false, reloadOnChange: true);
});


builder.ConfigureServices(services => { 

    // <ThrottlingTroll Egress Configuration>

    // Configuring a named HttpClient for egress throttling. Rules and limits taken from appsettings.json
    services.AddHttpClient("my-throttled-httpclient").AddThrottlingTrollMessageHandler();

    // Configuring a named HttpClient that does automatic retries with respect to Retry-After response header
    services.AddHttpClient("my-retrying-httpclient").AddThrottlingTrollMessageHandler(options =>
    {
        options.ResponseFabric = async (limitExceededResult, requestProxy, responseProxy, cancelToken) =>
        {
            var egressResponse = (IEgressHttpResponseProxy)responseProxy;

            egressResponse.ShouldRetry = true;
        };
    });

    // </ThrottlingTroll Egress Configuration>

});

builder.ConfigureFunctionsWorkerDefaults((hostBuilderContext, workerAppBuilder) => {

    // <ThrottlingTroll Ingress Configuration>

    workerAppBuilder.UseThrottlingTroll(hostBuilderContext);

    // Static programmatic configuration
    workerAppBuilder.UseThrottlingTroll(hostBuilderContext, options =>
    {
        options.Config = new ThrottlingTrollConfig
        {
            Rules = new[]
            {
            new ThrottlingTrollRule
            {
                UriPattern = "/fixed-window-1-request-per-2-seconds-configured-programmatically",
                LimitMethod = new FixedWindowRateLimitMethod
                {
                    PermitLimit = 1,
                    IntervalInSeconds = 2
                }
            }
        },

            // Specifying UniqueName is needed when multiple services store their
            // rate limit counters in the same cache instance, to prevent those services
            // from corrupting each other's counters. Otherwise you can skip it.
            UniqueName = "MyThrottledService1"
        };
    });

    // Dynamic programmatic configuration. Allows to adjust rules and limits without restarting the service.
    workerAppBuilder.UseThrottlingTroll(hostBuilderContext, options =>
    {
        options.GetConfigFunc = async () =>
        {
            // Loading settings from a custom file. You can instead load them from a database
            // or from anywhere else.

            string ruleFileName = Path.Combine(AppContext.BaseDirectory, "my-dynamic-throttling-rule.json");

            string ruleJson = await File.ReadAllTextAsync(ruleFileName);

            var rule = JsonSerializer.Deserialize<ThrottlingTrollRule>(ruleJson);

            return new ThrottlingTrollConfig
            {
                Rules = new[] { rule }
            };
        };

        // The above function will be periodically called every 5 seconds
        options.IntervalToReloadConfigInSeconds = 5;
    });

    // Demonstrates how to use custom response fabrics
    workerAppBuilder.UseThrottlingTroll(hostBuilderContext, options =>
    {
        options.Config = new ThrottlingTrollConfig
        {
            Rules = new[]
            {
                new ThrottlingTrollRule
                {
                    UriPattern = "/fixed-window-1-request-per-2-seconds-response-fabric",
                    LimitMethod = new FixedWindowRateLimitMethod
                    {
                        PermitLimit = 1,
                        IntervalInSeconds = 2
                    }
                }
            }
        };

        // Custom response fabric, returns 400 BadRequest + some custom content
        options.ResponseFabric = async (limitExceededResult, requestProxy, responseProxy, requestAborted) =>
        {
            responseProxy.StatusCode = (int)HttpStatusCode.BadRequest;

            responseProxy.SetHttpHeader("Retry-After", limitExceededResult.RetryAfterHeaderValue);

            await responseProxy.WriteAsync("Too many requests. Try again later.");
        };
    });

    // Demonstrates how to delay the response instead of returning 429
    workerAppBuilder.UseThrottlingTroll(hostBuilderContext, options =>
    {
        options.Config = new ThrottlingTrollConfig
        {
            Rules = new[]
            {
                new ThrottlingTrollRule
                {
                    UriPattern = "/fixed-window-1-request-per-2-seconds-delayed-response",
                    LimitMethod = new FixedWindowRateLimitMethod
                    {
                        PermitLimit = 1,
                        IntervalInSeconds = 2
                    }
                }
            }
        };

        // Custom response fabric, impedes the normal response for 3 seconds
        options.ResponseFabric = async (limitExceededResult, requestProxy, responseProxy, requestAborted) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3));

            var ingressResponse = (IIngressHttpResponseProxy)responseProxy;
            ingressResponse.ShouldContinueAsNormal = true;
        };
    });

    // Demonstrates how to use identity extractors
    workerAppBuilder.UseThrottlingTroll(hostBuilderContext, options =>
    {
        options.Config = new ThrottlingTrollConfig
        {
            Rules = new[]
            {
                new ThrottlingTrollRule
                {
                    UriPattern = "/fixed-window-3-requests-per-15-seconds-per-each-api-key",
                    LimitMethod = new FixedWindowRateLimitMethod
                    {
                        PermitLimit = 3,
                        IntervalInSeconds = 15
                    },

                    IdentityIdExtractor = request =>
                    {
                        // Identifying clients by their api-key
                        return ((IIncomingHttpRequestProxy)request).Request.Query["api-key"];
                    }
                }
            }
        };
    });


    // </ThrottlingTroll Ingress Configuration>

});



var host = builder.Build();
host.Run();