// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0
using System;

using cartservice.cartstore;
using cartservice.services;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Instrumentation.StackExchangeRedis;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OpenFeature;
using OpenFeature.Contrib.Providers.Flagd;
using OpenFeature.Contrib.Hooks.Otel;
using OpenTelemetry;


var builder = WebApplication.CreateBuilder(args);
string valkeyAddress = builder.Configuration["VALKEY_ADDR"];
if (string.IsNullOrEmpty(valkeyAddress))
{
    Console.WriteLine("VALKEY_ADDR environment variable is required.");
    Environment.Exit(1);
}

builder.Logging
    .AddOpenTelemetry(options => options.AddOtlpExporter())
    .AddConsole();

builder.Services.AddSingleton<ICartStore>(x =>
{
    var store = new ValkeyCartStore(x.GetRequiredService<ILogger<ValkeyCartStore>>(), valkeyAddress);
    store.Initialize();
    return store;
});

builder.Services.AddSingleton<IFeatureClient>(x =>
{
    var flagdProvider = new FlagdProvider();
    Api.Instance.SetProviderAsync(flagdProvider).GetAwaiter().GetResult();
    var client = Api.Instance.GetClient();
    return client;
});

// Setup OpenTelemetry Tracing
var tracerProvider = Sdk.CreateTracerProviderBuilder()
    .AddAspNetCoreInstrumentation()
    .AddHttpClientInstrumentation()
    .AddOtlpExporter()
    .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("cartservice"))
    .Build();

var tracer = tracerProvider.GetTracer("cartservice");

// Update CartService instantiation to pass Tracer
builder.Services.AddSingleton(x =>
    new CartService(
        x.GetRequiredService<ICartStore>(),
        new ValkeyCartStore(x.GetRequiredService<ILogger<ValkeyCartStore>>(), "badhost:1234"),
        x.GetRequiredService<IFeatureClient>()
    ));

Action<ResourceBuilder> appResourceBuilder =
    resource => resource
        .AddContainerDetector()
        .AddHostDetector();

builder.Services.AddOpenTelemetry()
    .ConfigureResource(appResourceBuilder)
    .WithTracing(tracerBuilder => tracerBuilder
        .AddRedisInstrumentation(
            options => options.SetVerboseDatabaseStatements = true)
        .AddAspNetCoreInstrumentation()
        .AddGrpcClientInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter())
    .WithMetrics(meterBuilder => meterBuilder
        .AddProcessInstrumentation()
        .AddRuntimeInstrumentation()
        .AddAspNetCoreInstrumentation()
        .AddOtlpExporter());

OpenFeature.Api.Instance.AddHooks(new TracingHook());

builder.Services.AddGrpc();
builder.Services.AddGrpcHealthChecks()
    .AddCheck("Sample", () => HealthCheckResult.Healthy());

var app = builder.Build();

var ValkeyCartStore = (ValkeyCartStore)app.Services.GetRequiredService<ICartStore>();
app.Services.GetRequiredService<StackExchangeRedisInstrumentation>().AddConnection(ValkeyCartStore.GetConnection());

// Server-side logic to extract X-Service-Name from incoming requests
app.Use(async (context, next) =>
{
    var span = tracer.StartActiveSpan("request.process");
    try
    {
        // Extract X-Service-Name from headers
        var serviceName = context.Request.Headers["X-Service-Name"].ToString();
        if (!string.IsNullOrEmpty(serviceName))
        {
            // Attach X-Service-Name to the current span
            span.SetAttribute("net.peer.name", serviceName);

            // add logging here
            Console.WriteLine($"Service name: {serviceName} added to span as net.peer.name");
        }

        await next();
    }
    finally
    {
        span.End();
    }
});

app.MapGrpcService<CartService>();
app.MapGrpcHealthChecksService();

app.MapGet("/", async context =>
{
    await context.Response.WriteAsync("Communication with gRPC endpoints must be made through a gRPC client. To learn how to create a client, visit: https://go.microsoft.com/fwlink/?linkid=2086909");
});

app.Run();
