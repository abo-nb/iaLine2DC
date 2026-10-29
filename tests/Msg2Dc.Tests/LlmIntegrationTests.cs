using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Msg2Dc;
using Msg2Dc.Configuration;
using Msg2Dc.Llm;
using Msg2Dc.Storage;
using Xunit;

namespace Msg2Dc.Tests;

public sealed class LlmIntegrationTests
{
    private const string AssistantJson = """
    {"items":[
      {"kind":"exam","subject":"数学","title":"三角函数小考","summary":"明天第一节考，范围 3-1 到 3-3","bodyMd":"带计算器","tags":["小考","第三章"],"links":[],"deadline":"2026-10-30T08:00:00+08:00","confidence":0.92},
      {"kind":"homework","subject":"英文","title":"英文报告","summary":"后天交","tags":["报告"],"links":["https://example.com"],"deadline":null,"confidence":0.8}
    ]}
    """;

    [Fact]
    public async Task SetModelThenClassifyReturnsStructuredItems()
    {
        var store = new MongoStore(Options.Create(new MongoOptions
        {
            ConnectionString = "mongodb://127.0.0.1:27017",
            Database = $"msg2dc_test_{Guid.NewGuid():N}"
        }));

        if (!await store.PingAsync(TimeSpan.FromSeconds(2), CancellationToken.None))
        {
            return;
        }

        try
        {
            await store.EnsureIndexesAsync(CancellationToken.None);

            await using var fake = new FakeOpenAiServer(AssistantJson);

            var llmOptions = Options.Create(new LlmOptions
            {
                TimeoutSeconds = 20,
                OpenAi = new OpenAiOptions
                {
                    BaseUrl = $"http://127.0.0.1:{fake.Port}/v1",
                    ApiKey = "sk-test"
                }
            });

            var selection = new LlmSelectionProvider(store, NullLogger<LlmSelectionProvider>.Instance);
            var factory = new LlmClientFactory(llmOptions, NullLoggerFactory.Instance);
            var switcher = new LlmModelSwitcher(store, factory, selection, llmOptions, NullLogger<LlmModelSwitcher>.Instance);
            var classifier = new LlmClassifier(store, factory, selection, llmOptions, NullLogger<LlmClassifier>.Instance);

            var switched = await switcher.SetModelAsync("openai", "fake-model", CancellationToken.None);
            Assert.True(switched.Ok, switched.Message);

            await store.Subjects.InsertManyAsync(new[]
            {
                new SubjectDoc { Name = "数学", Aliases = new List<string> { "数" }, CreatedAt = DateTime.UtcNow },
                new SubjectDoc { Name = "英文", CreatedAt = DateTime.UtcNow }
            });

            var result = await classifier.ClassifyAsync(
                new ClassifyRequest(
                    "数学",
                    "同学",
                    DateTime.UtcNow,
                    "明天第一节数学小考，范围 3-1 到 3-3，后天交英文报告",
                    new[] { new SubjectInfo("数学", new[] { "数" }), new SubjectInfo("英文", Array.Empty<string>()) },
                    Array.Empty<string>(),
                    "msg-test-1"),
                CancellationToken.None);

            Assert.True(result.Ok, result.Error);
            Assert.Equal(2, result.Items.Count);
            Assert.Equal("数学", result.Items[0].SubjectName);
            Assert.Equal("exam", result.Items[0].Kind);
            Assert.NotNull(result.Items[0].DeadlineAt);
            Assert.Equal("英文", result.Items[1].SubjectName);
            Assert.Equal("homework", result.Items[1].Kind);

            var logs = await store.LlmLogs.CountDocumentsAsync(FilterDefinition<LlmLogDoc>.Empty);
            Assert.True(logs >= 1);
        }
        finally
        {
            await store.DropDatabaseAsync(CancellationToken.None);
        }
    }
}

internal sealed class FakeOpenAiServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly string _body;

    public FakeOpenAiServer(string assistantContent)
    {
        _body = JsonSerializer.Serialize(new
        {
            id = "chatcmpl-test",
            @object = "chat.completion",
            created = 1700000000,
            model = "fake-model",
            choices = new[]
            {
                new
                {
                    index = 0,
                    message = new { role = "assistant", content = assistantContent },
                    finish_reason = "stop"
                }
            },
            usage = new { prompt_tokens = 10, completion_tokens = 10, total_tokens = 20 }
        });

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _loop = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                using (client)
                {
                    try
                    {
                        var stream = client.GetStream();
                        var headerBytes = new List<byte>();
                        var one = new byte[1];
                        while (headerBytes.Count < 65536)
                        {
                            var read = await stream.ReadAsync(one, _cts.Token);
                            if (read == 0)
                            {
                                break;
                            }

                            headerBytes.Add(one[0]);
                            var count = headerBytes.Count;
                            if (count >= 4 &&
                                headerBytes[count - 4] == (byte)'\r' && headerBytes[count - 3] == (byte)'\n' &&
                                headerBytes[count - 2] == (byte)'\r' && headerBytes[count - 1] == (byte)'\n')
                            {
                                break;
                            }
                        }

                        var headerText = Encoding.ASCII.GetString(headerBytes.ToArray());
                        var contentLength = 0;
                        foreach (var line in headerText.Split("\r\n"))
                        {
                            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                            {
                                int.TryParse(line[15..].Trim(), out contentLength);
                            }
                        }

                        var body = new byte[contentLength];
                        var offset = 0;
                        while (offset < contentLength)
                        {
                            var read = await stream.ReadAsync(body.AsMemory(offset, contentLength - offset), _cts.Token);
                            if (read == 0)
                            {
                                break;
                            }

                            offset += read;
                        }

                        var payload = Encoding.UTF8.GetBytes(_body);
                        var head = Encoding.ASCII.GetBytes(
                            $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
                        await stream.WriteAsync(head, _cts.Token);
                        await stream.WriteAsync(payload, _cts.Token);
                        await stream.FlushAsync(_cts.Token);
                    }
                    catch
                    {
                        // the test client may disconnect early; nothing to do
                    }
                }
            });
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try
        {
            await _loop;
        }
        catch
        {
            // ignored
        }

        _cts.Dispose();
    }
}
