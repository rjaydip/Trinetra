using System.Xml.Linq;

namespace Trinetra.Federation.Adapters.Onvif;

/// <summary>XML namespaces used by the ONVIF services we call.</summary>
internal static class Ns
{
    internal static readonly XNamespace Soap = "http://www.w3.org/2003/05/soap-envelope";
    internal static readonly XNamespace Wsa = "http://www.w3.org/2005/08/addressing";

    /// <summary>WS-Security extension namespace, for the UsernameToken header.</summary>
    internal static readonly XNamespace Wsse =
        "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd";

    /// <summary>WS-Security utility namespace, for the Created timestamp.</summary>
    internal static readonly XNamespace Wsu =
        "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd";

    internal static readonly XNamespace Device = "http://www.onvif.org/ver10/device/wsdl";
    internal static readonly XNamespace Media = "http://www.onvif.org/ver10/media/wsdl";
    internal static readonly XNamespace Media2 = "http://www.onvif.org/ver20/media/wsdl";
    internal static readonly XNamespace Events = "http://www.onvif.org/ver10/events/wsdl";
    internal static readonly XNamespace Schema = "http://www.onvif.org/ver10/schema";

    internal static readonly XNamespace WsNt = "http://docs.oasis-open.org/wsn/b-2";
    internal static readonly XNamespace WsTop = "http://docs.oasis-open.org/wsn/t-1";

    internal const string PasswordDigestType =
        "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordDigest";

    internal const string Base64BinaryType =
        "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-soap-message-security-1.0#Base64Binary";
}
