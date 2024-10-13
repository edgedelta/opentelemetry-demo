// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Oteldemo;
using OpenTelemetry.Trace;

namespace AccountingService;

internal class Consumer : IDisposable
{
    private const string TopicName = "orders";

    private ILogger _logger;
    private IConsumer<string, byte[]> _consumer;
    private bool _isListening;

    public Consumer(ILogger<Consumer> logger)
    {
        _logger = logger;

        var servers = Environment.GetEnvironmentVariable("KAFKA_SERVICE_ADDR")
            ?? throw new ArgumentNullException("KAFKA_SERVICE_ADDR");

        _consumer = BuildConsumer(servers);
        _consumer.Subscribe(TopicName);

        _logger.LogInformation($"Connecting to Kafka: {servers}");
    }

    public void StartListening()
    {
        _isListening = true;

        try
        {
            while (_isListening)
            {
                try
                {
                    var consumeResult = _consumer.Consume();

                    // Extract the X-Service-Name from the OTEL_SERVICE_NAME environment variable
                    // Default to "opentelemetry-demo-accountingservice" if not set
                    var serviceName = Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME")
                        ?? $"opentelemetry-demo-accountingservice";

                    // Start a new OpenTelemetry span for processing each message
                    using (var tracer = Sdk.CreateTracerProviderBuilder().Build().GetTracer("accountingservice"))
                    {
                        var span = tracer.StartActiveSpan("kafka.consume");
                        span.SetAttribute("net.peer.name", "opentelemetry-demo-kafka");
                        try
                        {
                            // Adding X-Service-Name to the Kafka message headers
                            consumeResult.Message.Headers.Add(new Header("X-Service-Name", System.Text.Encoding.UTF8.GetBytes(serviceName)));

                            ProcessMessage(consumeResult.Message);
                        }
                        catch (Exception ex)
                        {
                            span.RecordException(ex);
                            throw;
                        }
                        finally
                        {
                            span.End();
                        }

                        ProcessMessage(consumeResult.Message);
                    }
                catch (ConsumeException e)
                {
                    _logger.LogError(e, "Consume error: {0}", e.Error.Reason);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Closing consumer");

            _consumer.Close();
        }
    }

    private void ProcessMessage(Message<string, byte[]> message)
    {
        try
        {
            var order = OrderResult.Parser.ParseFrom(message.Value);

            Log.OrderReceivedMessage(_logger, order);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Order parsing failed:");
        }
    }

    private IConsumer<string, byte[]> BuildConsumer(string servers)
    {
        var conf = new ConsumerConfig
        {
            GroupId = $"accountingservice",
            BootstrapServers = servers,
            // https://github.com/confluentinc/confluent-kafka-dotnet/tree/07de95ed647af80a0db39ce6a8891a630423b952#basic-consumer-example
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = true
        };

        return new ConsumerBuilder<string, byte[]>(conf)
            .Build();
    }

    public void Dispose()
    {
        _isListening = false;
        _consumer?.Dispose();
    }
}
