using Soenneker.Tests.FixturedUnit;
using Xunit;

namespace Soenneker.Twilio.Runners.OpenApiClient.Tests;

[Collection("Collection")]
public sealed class TwilioOpenApiClientRunnerTests : FixturedUnitTest
{
    public TwilioOpenApiClientRunnerTests(Fixture fixture, ITestOutputHelper output) : base(fixture, output)
    {
    }

    [Fact]
    public void Default()
    {

    }
}
