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
}
