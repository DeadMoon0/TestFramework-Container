using System.Linq;
using TestFramework.Container.Azure;
using TestFramework.Core.Conventions;
using Xunit;
using Xunit.Abstractions;

namespace TestFramework.Container.Azure.Tests;

/// <summary>
/// The family's rules, checked against this package rather than trusted to have been followed.
/// </summary>
/// <remarks>
/// These live in Core (<see cref="StepConventions"/>) and every package calls them on its own assembly: a
/// rule only Core's suite enforces is a rule only Core follows.
/// </remarks>
public class ConventionTests(ITestOutputHelper output)
{
    [Fact]
    public void EveryStepInThisPackageClonesItself()
    {
        // A step that inherits a concrete base class's Clone() runs as that base class and silently loses
        // whatever it added. The compiler only catches the case where the base is abstract.
        ConventionReport report = StepConventions.AssertEveryStepClonesItself(typeof(ServiceBusTopologySerializer).Assembly);

        output.WriteLine(report.ToString());
    }

    [Fact]
    public void FreezingCascadesThroughThisPackagesParts()
    {
        ConventionReport report = StepConventions.AssertFreezingCascades(typeof(ServiceBusTopologySerializer).Assembly);

        output.WriteLine(report.ToString());
        foreach (string skipped in report.Skipped)
        {
            output.WriteLine($"  skipped {skipped}");
        }
    }

    [Fact]
    public void ThisPackageSerialisesWithOneJsonLibrary()
    {
        // The family picked Newtonsoft.Json. Two libraries mean two sets of attributes, two notions of what
        // null means, and values that survive one round trip but not the other - and the seam shows up as a
        // bug in whichever package sits between them. Checked against the compiled assembly, because a
        // stray using is invisible in a diff.
        Assert.DoesNotContain(
            "System.Text.Json",
            typeof(ServiceBusTopologySerializer).Assembly.GetReferencedAssemblies().Select(static reference => reference.Name));
    }

    [Fact]
    public void ThisPackageKeepsItsInternalsToItself()
    {
        // Every package is a stranger to every other. A grant to another package is a private handshake:
        // two packages understand each other and a third cannot join, so what the favoured one may do stops
        // being what any of them may do - and the grant hides the fact that a surface is missing.
        ConventionReport report = StepConventions.AssertNoPackageSeesAnothersInternals(typeof(ServiceBusTopologySerializer).Assembly);

        output.WriteLine(report.ToString());
    }
}
