using Docker.DotNet.Models;
using System.Collections.Generic;
using System.Net;
using Xunit;

namespace TestFramework.Container.Tests;

public class ContainerPortBindingTests
{
    [Fact]
    public void Apply_BindingWithoutHostAddress_TakesTheResolvedAddress()
    {
        CreateContainerParameters parameters = CreateParameters(new PortBinding { HostPort = string.Empty });

        ContainerPortBinding.Apply(parameters);

        Assert.Equal(ContainerPortBinding.HostIp, parameters.HostConfig.PortBindings["80/tcp"][0].HostIP);
    }

    [Fact]
    public void Apply_BindingWithItsOwnHostAddress_IsLeftAlone()
    {
        CreateContainerParameters parameters = CreateParameters(new PortBinding { HostIP = "10.1.2.3", HostPort = string.Empty });

        ContainerPortBinding.Apply(parameters);

        Assert.Equal("10.1.2.3", parameters.HostConfig.PortBindings["80/tcp"][0].HostIP);
    }

    [Fact]
    public void Apply_NoHostConfig_DoesNothing()
    {
        CreateContainerParameters parameters = new();

        ContainerPortBinding.Apply(parameters);

        Assert.Null(parameters.HostConfig);
    }

    [Fact]
    public void HostIp_IsAnAddressPortsCanBeBoundTo()
    {
        Assert.True(IPAddress.TryParse(ContainerPortBinding.HostIp, out _));
    }

    private static CreateContainerParameters CreateParameters(PortBinding binding)
        => new()
        {
            HostConfig = new HostConfig
            {
                PortBindings = new Dictionary<string, IList<PortBinding>>
                {
                    ["80/tcp"] = [binding],
                },
            },
        };
}
