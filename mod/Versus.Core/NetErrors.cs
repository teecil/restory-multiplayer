using System;
using System.Net.Sockets;

namespace RestoryVersus.Core
{
    // Exceptions from hosting or joining, as error codes the menu turns into sentences (VersusMenu.ErrorText).
    public static class NetErrors
    {
        public static string Classify(Exception e, bool hosting)
        {
            if (e is SocketException se)
            {
                if (se.SocketErrorCode == SocketError.AddressAlreadyInUse) return "portBusy";
                if (se.SocketErrorCode == SocketError.HostNotFound || se.SocketErrorCode == SocketError.NoData) return "badAddress";
            }
            if (!hosting && (e is FormatException || e is OverflowException || e is ArgumentException || e is IndexOutOfRangeException)) return "badAddress";
            return hosting ? "hostFailed" : "joinFailed";
        }
    }

    // The menu's address field takes the host's IP only; the port is the mod's own (Network.Port). What players
    // paste from a tunnel or chat ("1.2.3.4:27515", " 1.2.3.4 ", "udp://1.2.3.4/") is cut down to the host.
    public static class HostAddress
    {
        public static string Clean(string text)
        {
            var s = (text ?? "").Trim().Replace(" ", "");
            int scheme = s.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0) s = s.Substring(scheme + 3);
            int slash = s.IndexOf('/');
            if (slash >= 0) s = s.Substring(0, slash);
            int colon = s.IndexOf(':');
            if (colon >= 0 && colon == s.LastIndexOf(':')) s = s.Substring(0, colon);
            return s;
        }
    }
}
