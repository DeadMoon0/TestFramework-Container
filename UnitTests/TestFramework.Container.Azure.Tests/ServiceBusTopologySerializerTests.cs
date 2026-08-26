using System.Collections.Generic;
using TestFramework.Container.Azure;
using Xunit;

namespace TestFramework.Container.Azure.Tests;

/// <summary>
/// The exact file the Service Bus emulator reads.
/// </summary>
/// <remarks>
/// <para>
/// The emulator loads this at startup and fails to host anything if a name is spelled differently, so the
/// text matters rather than the shape. Nothing pinned it before: the only check was a smoke test that needs
/// a Docker daemon, which means the file's contents were unverified anywhere a daemon was missing - and
/// that is exactly where a JSON library swap would go unnoticed.
/// </para>
/// <para>
/// So this pins the output verbatim. It was written against the System.Text.Json implementation and kept
/// passing across the move to Newtonsoft, which is what makes that move a verified one instead of a hope.
/// </para>
/// </remarks>
public class ServiceBusTopologySerializerTests
{
    [Fact]
    public void Serialize_WritesTheEmulatorsOwnShapeAndNames()
    {
        ServiceBusEmulatorTopologyDefinition topology = new(
            new ServiceBusEmulatorUserConfigDefinition(
                [
                    new ServiceBusEmulatorNamespaceDefinition(
                        "sbemulatorns",
                        [new ServiceBusEmulatorQueueDefinition("orders")],
                        [
                            new ServiceBusEmulatorTopicDefinition(
                                "events",
                                [new ServiceBusEmulatorSubscriptionDefinition("audit")]),
                        ]),
                ],
                new ServiceBusEmulatorLoggingDefinition("File")));

        string json = ServiceBusTopologySerializer.Serialize(topology);

        // Both sides normalised: the emulator does not care which line ending it reads, and this file's
        // own endings should not decide whether the test passes.
        string expected = """
            {
              "UserConfig": {
                "Namespaces": [
                  {
                    "Name": "sbemulatorns",
                    "Queues": [
                      {
                        "Name": "orders"
                      }
                    ],
                    "Topics": [
                      {
                        "Name": "events",
                        "Subscriptions": [
                          {
                            "Name": "audit"
                          }
                        ]
                      }
                    ]
                  }
                ],
                "Logging": {
                  "Type": "File"
                }
              }
            }
            """;

        Assert.Equal(expected.ReplaceLineEndings("\n").TrimEnd(), json.ReplaceLineEndings("\n").TrimEnd());
    }
}
