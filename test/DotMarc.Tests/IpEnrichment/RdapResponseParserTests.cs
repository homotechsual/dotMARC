// test/DotMarc.Tests/IpEnrichment/RdapResponseParserTests.cs
using DotMarc.IpEnrichment;
using Xunit;

namespace DotMarc.Tests.IpEnrichment;

public sealed class RdapResponseParserTests
{
    [Fact]
    public void Parse_ExtractsOrganizationAndCountry_FromAnArinStyleResponse()
    {
        const string json = """
            {
                "objectClassName": "ip network",
                "name": "GOOGLE",
                "country": "US",
                "entities": [
                    {
                        "objectClassName": "entity",
                        "roles": ["registrant"],
                        "vcardArray": ["vcard", [
                            ["version", {}, "text", "4.0"],
                            ["fn", {}, "text", "Google LLC"]
                        ]]
                    }
                ]
            }
            """;

        var (organization, country) = RdapResponseParser.Parse(json);

        Assert.Equal("Google LLC", organization);
        Assert.Equal("US", country);
    }

    [Fact]
    public void Parse_FallsBackToTheFirstEntity_WhenNoneHasTheRegistrantRole()
    {
        const string json = """
            {
                "objectClassName": "ip network",
                "name": "EU-ZZZ-20221101",
                "entities": [
                    {
                        "objectClassName": "entity",
                        "roles": ["administrative", "technical"],
                        "vcardArray": ["vcard", [
                            ["version", {}, "text", "4.0"],
                            ["fn", {}, "text", "Example Org B.V."]
                        ]]
                    }
                ]
            }
            """;

        var (organization, country) = RdapResponseParser.Parse(json);

        Assert.Equal("Example Org B.V.", organization);
        Assert.Null(country);
    }

    [Fact]
    public void Parse_PrefersTheRegistrantEntity_WhenMultipleEntitiesArePresent()
    {
        const string json = """
            {
                "objectClassName": "ip network",
                "country": "GB",
                "entities": [
                    {
                        "objectClassName": "entity",
                        "roles": ["technical"],
                        "vcardArray": ["vcard", [["fn", {}, "text", "Some ISP NOC"]]]
                    },
                    {
                        "objectClassName": "entity",
                        "roles": ["registrant"],
                        "vcardArray": ["vcard", [["fn", {}, "text", "Actual Owner Ltd"]]]
                    }
                ]
            }
            """;

        var (organization, country) = RdapResponseParser.Parse(json);

        Assert.Equal("Actual Owner Ltd", organization);
        Assert.Equal("GB", country);
    }

    [Fact]
    public void Parse_ReturnsNulls_WhenThereIsNoUsableData()
    {
        const string json = """{ "objectClassName": "ip network", "name": "RESERVED-BLOCK" }""";

        var (organization, country) = RdapResponseParser.Parse(json);

        Assert.Null(organization);
        Assert.Null(country);
    }

    [Fact]
    public void Parse_ReturnsNulls_WhenEntitiesArrayIsEmpty()
    {
        const string json = """{ "objectClassName": "ip network", "country": "DE", "entities": [] }""";

        var (organization, country) = RdapResponseParser.Parse(json);

        Assert.Null(organization);
        Assert.Equal("DE", country);
    }
}
