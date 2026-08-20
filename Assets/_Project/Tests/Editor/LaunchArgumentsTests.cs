using ChopChop.Core;
using ChopChop.Networking;
using NUnit.Framework;

namespace ChopChop.Tests.Editor
{
    /// <summary>
    /// Cold-start invites are easy to break and hard to notice, since the failure
    /// mode is "the game opens to the menu" rather than an error.
    /// </summary>
    public sealed class LaunchArgumentsTests
    {
        [Test]
        public void ParsesIdFollowingFlag()
        {
            string[] args = { "ChopChop.exe", "+connect_lobby", "109775241009964321" };

            Assert.IsTrue(LaunchArguments.TryGetConnectLobby(args, out ulong id));
            Assert.AreEqual(109775241009964321UL, id);
        }

        [Test]
        public void IsCaseInsensitive()
        {
            string[] args = { "+CONNECT_LOBBY", "42" };

            Assert.IsTrue(LaunchArguments.TryGetConnectLobby(args, out ulong id));
            Assert.AreEqual(42UL, id);
        }

        [Test]
        public void FailsWhenFlagAbsent()
        {
            string[] args = { "ChopChop.exe", "-batchmode" };

            Assert.IsFalse(LaunchArguments.TryGetConnectLobby(args, out ulong id));
            Assert.AreEqual(0UL, id);
        }

        [Test]
        public void FailsWhenFlagIsFinalArgument()
        {
            string[] args = { "ChopChop.exe", "+connect_lobby" };

            Assert.IsFalse(LaunchArguments.TryGetConnectLobby(args, out _));
        }

        [Test]
        public void FailsWhenIdIsNotNumeric()
        {
            string[] args = { "+connect_lobby", "not-an-id" };

            Assert.IsFalse(LaunchArguments.TryGetConnectLobby(args, out _));
        }

        [Test]
        public void FailsWhenIdIsZero()
        {
            string[] args = { "+connect_lobby", "0" };

            Assert.IsFalse(LaunchArguments.TryGetConnectLobby(args, out _));
        }

        [Test]
        public void HandlesNullArgs()
        {
            Assert.IsFalse(LaunchArguments.TryGetConnectLobby(null, out _));
        }
    }

    /// <summary>
    /// The role is chosen at runtime rather than by build target, so these flags are
    /// the only thing standing between "dedicated server" and "silently launched a
    /// client that serves nobody".
    /// </summary>
    public sealed class LaunchArgumentRoleTests
    {
        [Test]
        public void ServerFlagSelectsServer()
        {
            Assert.IsTrue(LaunchArguments.TryGetRole(new[] { "ChopChop.exe", "-server" }, out AppRole role));
            Assert.AreEqual(AppRole.Server, role);
        }

        [Test]
        public void ConnectFlagSelectsClient()
        {
            Assert.IsTrue(LaunchArguments.TryGetRole(new[] { "-connect", "10.0.0.5" }, out AppRole role));
            Assert.AreEqual(AppRole.Client, role);
        }

        [Test]
        public void ServerWinsOverConnect()
        {
            // A process told to serve must serve. Quietly becoming a client instead
            // would be near-impossible to diagnose on a headless box.
            Assert.IsTrue(LaunchArguments.TryGetRole(new[] { "-connect", "10.0.0.5", "-server" }, out AppRole role));
            Assert.AreEqual(AppRole.Server, role);
        }

        [Test]
        public void NoRoleFlagsLeavesTheDecisionToTheCaller()
        {
            Assert.IsFalse(LaunchArguments.TryGetRole(new[] { "ChopChop.exe", "-batchmode" }, out _));
            Assert.IsFalse(LaunchArguments.TryGetRole(null, out _));
        }

        [Test]
        public void ParsesHostWithEmbeddedPort()
        {
            Assert.IsTrue(LaunchArguments.TryGetConnect(new[] { "-connect", "example.com:7777" }, 7770,
                out string host, out ushort port));
            Assert.AreEqual("example.com", host);
            Assert.AreEqual(7777, port);
        }

        [Test]
        public void BareHostKeepsTheDefaultPort()
        {
            Assert.IsTrue(LaunchArguments.TryGetConnect(new[] { "-connect", "example.com" }, 7770,
                out string host, out ushort port));
            Assert.AreEqual("example.com", host);
            Assert.AreEqual(7770, port, "an explicit -port should still be able to supply this");
        }

        [Test]
        public void RejectsMalformedAddresses()
        {
            Assert.IsFalse(LaunchArguments.TryGetConnect(new[] { "-connect", "example.com:" }, 7770, out _, out _),
                "trailing colon");
            Assert.IsFalse(LaunchArguments.TryGetConnect(new[] { "-connect", "example.com:banana" }, 7770, out _, out _),
                "non-numeric port");
            Assert.IsFalse(LaunchArguments.TryGetConnect(new[] { "-connect", "example.com:0" }, 7770, out _, out _),
                "port zero");
            Assert.IsFalse(LaunchArguments.TryGetConnect(new[] { "-connect", ":7777" }, 7770, out _, out _),
                "no host");
            Assert.IsFalse(LaunchArguments.TryGetConnect(new[] { "-connect" }, 7770, out _, out _),
                "flag is final argument");
        }

        [Test]
        public void ParsesExplicitPort()
        {
            Assert.IsTrue(LaunchArguments.TryGetPort(new[] { "-server", "-port", "9999" }, out ushort port));
            Assert.AreEqual(9999, port);

            Assert.IsFalse(LaunchArguments.TryGetPort(new[] { "-server" }, out _));
            Assert.IsFalse(LaunchArguments.TryGetPort(new[] { "-port", "0" }, out _), "port zero is not usable");
            Assert.IsFalse(LaunchArguments.TryGetPort(new[] { "-port", "70000" }, out _), "out of range");
        }

        [Test]
        public void RoleHelpersDescribeWhatRuns()
        {
            Assert.IsTrue(AppRole.Server.RunsServer());
            Assert.IsFalse(AppRole.Server.RunsClient());

            Assert.IsFalse(AppRole.Client.RunsServer());
            Assert.IsTrue(AppRole.Client.RunsClient());

            Assert.IsTrue(AppRole.HostedServer.RunsServer());
            Assert.IsTrue(AppRole.HostedServer.RunsClient());
        }
    }

    /// <summary>
    /// The start screen must not swallow a launch that already knew what it wanted.
    ///
    /// Worth pinning because the failure is silent in the worst possible place: a
    /// headless server that stops at a menu does not error, it just never appears, and
    /// the only symptom is that nobody can connect (TECH 15).
    /// </summary>
    public sealed class StartScreenGateTests
    {
        [Test]
        public void DedicatedServerNeverWaits()
        {
            Assert.IsFalse(AppRole.Server.WaitsOnStartScreen(enabled: true, launchedWithIntent: false),
                "a dedicated server has no screen and nobody to press a button");
        }

        [Test]
        public void CommandLineIntentSkipsTheMenu()
        {
            Assert.IsFalse(AppRole.Client.WaitsOnStartScreen(enabled: true, launchedWithIntent: true),
                "-connect already answered the question the menu asks");

            Assert.IsFalse(AppRole.HostedServer.WaitsOnStartScreen(enabled: true, launchedWithIntent: true));
        }

        [Test]
        public void APlainLaunchWaits()
        {
            Assert.IsTrue(AppRole.Client.WaitsOnStartScreen(enabled: true, launchedWithIntent: false));
            Assert.IsTrue(AppRole.HostedServer.WaitsOnStartScreen(enabled: true, launchedWithIntent: false));
        }

        [Test]
        public void DisablingTheScreenBeatsEverything()
        {
            Assert.IsFalse(AppRole.Client.WaitsOnStartScreen(enabled: false, launchedWithIntent: false));
            Assert.IsFalse(AppRole.HostedServer.WaitsOnStartScreen(enabled: false, launchedWithIntent: false));
        }
    }
}
