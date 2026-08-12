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
}
