using Soenneker.Tests.HostedUnit;

namespace Soenneker.Twilio.Runners.OpenApiClient.Tests;

[ClassDataSource<Host>(Shared = SharedType.PerTestSession)]
public sealed class TwilioOpenApiClientRunnerTests : HostedUnitTest
{
    public TwilioOpenApiClientRunnerTests(Host host) : base(host)
    {
    }

    [Test]
    public void Default()
    {

    }
}
