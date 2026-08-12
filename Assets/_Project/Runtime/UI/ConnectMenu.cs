using ChopChop.Core;
using ChopChop.Networking;
using FishNet.Managing;
using UnityEngine;

namespace ChopChop.UI
{
    /// <summary>
    /// Type an address, press Connect. Shown only while this client is not connected to
    /// anything.
    ///
    /// Deliberately IMGUI and deliberately ugly. This is plumbing that exists so a
    /// dedicated server can be reached at all, and it should be thrown away wholesale
    /// when there is a real menu — building it out of a Canvas and prefabs now would
    /// only make it harder to delete.
    /// </summary>
    public sealed class ConnectMenu : MonoBehaviour
    {
        [SerializeField] private NetworkManager _networkManager;
        [SerializeField] private string _address = "127.0.0.1";
        [SerializeField] private ushort _port = 7770;

        [Tooltip("Also show while a server is running in this process. Off by default, " +
                 "since a hosted server is already where it wants to be.")]
        [SerializeField] private bool _showWhileServing;

        private SessionCoordinator _session;
        private string _addressField;
        private string _portField;
        private string _status;

        private void Awake()
        {
            _addressField = _address;
            _portField = _port.ToString();

            if (_networkManager == null)
                _networkManager = FindObjectOfType<NetworkManager>();
        }

        private bool ShouldShow()
        {
            if (_networkManager == null)
                return false;

            if (_networkManager.IsClientStarted)
                return false;

            return _showWhileServing || !_networkManager.IsServerStarted;
        }

        private void OnGUI()
        {
            if (!ShouldShow())
                return;

            const float width = 260f;
            const float height = 132f;

            GUILayout.BeginArea(new Rect(12f, 12f, width, height), GUI.skin.box);
            GUILayout.Label("Connect to server");

            GUILayout.BeginHorizontal();
            GUILayout.Label("Address", GUILayout.Width(60f));
            _addressField = GUILayout.TextField(_addressField ?? string.Empty);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Port", GUILayout.Width(60f));
            _portField = GUILayout.TextField(_portField ?? string.Empty);
            GUILayout.EndHorizontal();

            if (GUILayout.Button("Connect"))
                Connect();

            if (!string.IsNullOrEmpty(_status))
                GUILayout.Label(_status);

            GUILayout.EndArea();
        }

        private void Connect()
        {
            if (string.IsNullOrWhiteSpace(_addressField))
            {
                _status = "Enter an address.";
                return;
            }

            if (!ushort.TryParse(_portField, out ushort port) || port == 0)
            {
                _status = "Port must be 1-65535.";
                return;
            }

            if (_session == null && !ServiceLocator.TryGet(out _session))
            {
                // Nothing has booted; there is no session to drive.
                _status = "No session available.";
                return;
            }

            _status = $"Connecting to {_addressField}:{port}...";
            _session.ConnectClient(_addressField.Trim(), port);
        }
    }
}
