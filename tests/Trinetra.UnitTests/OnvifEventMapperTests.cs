using System.Xml.Linq;
using Shouldly;
using Trinetra.Federation.Adapters.Onvif;
using Trinetra.Federation.Core.Events;

namespace Trinetra.UnitTests;

public sealed class OnvifEventMapperTests
{
    [Theory]
    [InlineData("tns1:RuleEngine/CellMotionDetector/Motion", EventType.MotionDetected)]
    [InlineData("tns1:VideoSource/MotionAlarm", EventType.MotionDetected)]
    [InlineData("tns1:RuleEngine/TamperDetector/Tamper", EventType.TamperDetected)]
    [InlineData("tns1:VideoSource/SignalLoss", EventType.VideoLoss)]
    [InlineData("tns1:RuleEngine/LineDetector/Crossed", EventType.LineCrossed)]
    [InlineData("tns1:RuleEngine/FieldDetector/ObjectsInside", EventType.IntrusionDetected)]
    [InlineData("tns1:RuleEngine/LoiteringDetector/Alarm", EventType.LoiteringDetected)]
    [InlineData("tns1:VideoAnalytics/LicensePlateRecognition", EventType.AnprDetection)]
    [InlineData("tns1:VideoSource/ImageTooDark/AnalyticsService", EventType.SceneChange)]
    public void MapTopic_ClassifiesKnownTopics(string topic, EventType expected) =>
        OnvifEventMapper.MapTopic(topic).ShouldBe(expected);

    [Theory]
    [InlineData("tns1:Monitoring/SomeVendorExtension")]
    [InlineData("")]
    public void MapTopic_FallsBackToVendorSpecific(string topic) =>
        OnvifEventMapper.MapTopic(topic).ShouldBe(EventType.VendorSpecific);

    [Fact]
    public void MapTopic_PrefersSpecificOverGeneric()
    {
        // "Motion" appears inside the tamper-adjacent topic tree on some firmware; the more
        // specific classification must win or every tamper alert is filed as routine motion.
        OnvifEventMapper.MapTopic("tns1:RuleEngine/TamperDetector/MotionTamper")
            .ShouldBe(EventType.TamperDetected);
    }

    [Fact]
    public void ReadItems_ExtractsSimpleItemPairs()
    {
        var data = XElement.Parse("""
            <Data xmlns="http://www.onvif.org/ver10/schema">
              <SimpleItem Name="State" Value="true" />
              <SimpleItem Name="PlateNumber" Value="GJ05AB1234" />
            </Data>
            """);

        var items = OnvifEventMapper.ReadItems(data);

        items["State"].ShouldBe("true");
        items["PlateNumber"].ShouldBe("GJ05AB1234");
    }

    [Fact]
    public void ExtractObjectReference_PrefersPlateNumber()
    {
        var data = new Dictionary<string, string> { ["PlateNumber"] = "GJ05AB1234" };

        OnvifEventMapper.ExtractObjectReference(data).ShouldBe("GJ05AB1234");
    }

    [Fact]
    public void ExtractObjectReference_IgnoresTrackIds()
    {
        // A track id is only meaningful within one camera. Promoting it to ObjectReference would
        // let correlation join unrelated subjects that happen to share a counter value.
        var data = new Dictionary<string, string> { ["ObjectTrackId"] = "17", ["TrackId"] = "17" };

        OnvifEventMapper.ExtractObjectReference(data).ShouldBeNull();
    }

    [Theory]
    [InlineData("0.96", 0.96)]
    [InlineData("96", 0.96)]
    [InlineData("150", 1.0)]
    public void ExtractConfidence_NormalisesBothReportingScales(string raw, double expected)
    {
        // Devices report confidence as 0..1 or 0..100; both occur in the field.
        var data = new Dictionary<string, string> { ["Likelihood"] = raw };

        OnvifEventMapper.ExtractConfidence(data)!.Value.ShouldBe(expected, tolerance: 0.001);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("Inactive", false)]
    [InlineData("Active", true)]
    public void IsActiveState_DistinguishesBothEdgesOfAPropertyEvent(string state, bool expected)
    {
        // ONVIF property events fire on raise and clear under one topic. Treating both as
        // "detected" would double event volume and invert every duration calculation.
        var data = new Dictionary<string, string> { ["State"] = state };

        OnvifEventMapper.IsActiveState(data).ShouldBe(expected);
    }

    [Fact]
    public void IsActiveState_TreatsOneShotEventsAsActive()
    {
        OnvifEventMapper.IsActiveState(new Dictionary<string, string>()).ShouldBeTrue();
    }
}
