using ChopChop.Core;
using UnityEngine;

namespace ChopChop.UI
{
    /// <summary>
    /// The first thing you see: start a world, join someone else's, or leave.
    ///
    /// **Deliberately IMGUI and deliberately plain.** This is the screen most likely to
    /// be replaced wholesale once there is a real menu, and building it out of a Canvas
    /// or a UXML tree now would only make it harder to throw away. It does need to be
    /// *correct* though, because it is the first thing a new player touches — so the
    /// failure paths matter more here than the styling does.
    ///
    /// Drives nothing itself. It asks <see cref="ISessionLauncher"/> to start a session
    /// and watches <see cref="AppStateMachine"/> to know when to get out of the way.
    /// </summary>
    public sealed class StartScreen : MonoBehaviour
    {
        private enum Page
        {
            Main,
            Join,
        }

        [SerializeField] private string _title = "CHOPCHOP";

        [Tooltip("How long to sit on 'Connecting…' before offering a way back. A refused " +
                 "connection is silent, so without this the screen would hang forever.")]
        [SerializeField] private float _connectTimeoutSeconds = 10f;

        private AppStateMachine _state;
        private ISessionLauncher _launcher;

        private Page _page = Page.Main;
        private string _address = "";
        private string _status = "";
        private float _connectingSince = -1f;

        private GUIStyle _titleStyle;
        private GUIStyle _statusStyle;
        private Texture2D _backdrop;

        private void OnDisable()
        {
            if (_backdrop != null)
                DestroyImmediate(_backdrop);

            _backdrop = null;
        }

        private void OnGUI()
        {
            if (!TryResolve())
                return;

            switch (_state.Current)
            {
                case AppState.Menu:
                    DrawMenu();
                    break;

                case AppState.Connecting:
                    DrawConnecting();
                    break;
            }
        }

        private bool TryResolve()
        {
            if (_state == null)
                ServiceLocator.TryGet(out _state);

            if (_launcher == null && ServiceLocator.TryGet(out _launcher))
                _address = _launcher.DefaultAddress;

            return _state != null && _launcher != null;
        }

        private void DrawMenu()
        {
            _connectingSince = -1f;

            using (Layout(300f, _page == Page.Main ? 260f : 240f))
            {
                GUILayout.Label(_title, TitleStyle());
                GUILayout.Space(18f);

                if (_page == Page.Main)
                    DrawMainPage();
                else
                    DrawJoinPage();

                if (!string.IsNullOrEmpty(_status))
                {
                    GUILayout.Space(10f);
                    GUILayout.Label(_status, StatusStyle());
                }
            }
        }

        private void DrawMainPage()
        {
            if (GUILayout.Button("New Game", GUILayout.Height(38f)))
            {
                _status = "";
                _launcher.HostNewGame();
            }

            GUILayout.Space(8f);

            if (GUILayout.Button("Join Game", GUILayout.Height(38f)))
            {
                _status = "";
                _page = Page.Join;
            }

            GUILayout.Space(8f);

            if (GUILayout.Button("Exit", GUILayout.Height(38f)))
                Quit();
        }

        private void DrawJoinPage()
        {
            GUILayout.Label("Host address");

            _address = GUILayout.TextField(_address ?? "", 64, GUILayout.Height(24f));

            bool submitted = Event.current.type == EventType.KeyDown
                             && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter);

            GUILayout.Space(10f);

            if (GUILayout.Button("Connect", GUILayout.Height(34f)) || submitted)
            {
                if (string.IsNullOrWhiteSpace(_address))
                {
                    _status = "Enter an address first.";
                }
                else
                {
                    _status = "";
                    _launcher.JoinGame(_address);
                }
            }

            GUILayout.Space(6f);

            if (GUILayout.Button("Back", GUILayout.Height(28f)))
            {
                _status = "";
                _page = Page.Main;
            }
        }

        private void DrawConnecting()
        {
            if (_connectingSince < 0f)
                _connectingSince = Time.realtimeSinceStartup;

            float elapsed = Time.realtimeSinceStartup - _connectingSince;

            using (Layout(300f, 130f))
            {
                GUILayout.Label(_title, TitleStyle());
                GUILayout.Space(18f);
                GUILayout.Label(elapsed < _connectTimeoutSeconds ? "Connecting…" : "No answer.", StatusStyle());

                /* Only offered once it has clearly failed. A cancel button available from
                 * the first frame invites people to press it during a connection that was
                 * going to succeed. */
                if (elapsed < _connectTimeoutSeconds)
                    return;

                GUILayout.Space(12f);

                if (GUILayout.Button("Back", GUILayout.Height(30f)))
                {
                    _status = "Could not reach that host.";
                    _page = Page.Main;
                    _connectingSince = -1f;
                    _state.Set(AppState.Menu);
                }
            }
        }

        /// <summary>A centred column, so the layout does not have to be hand-positioned twice.</summary>
        private GUILayout.AreaScope Layout(float width, float height)
        {
            Rect area = new((Screen.width - width) * 0.5f, (Screen.height - height) * 0.5f, width, height);

            /* Backing panel, because the menu sits over whatever the boot scene renders.
             * Plain labels were nearly invisible against a bright sky, and a menu you
             * cannot read is worse than an ugly one. */
            Rect panel = new(area.x - 24f, area.y - 24f, area.width + 48f, area.height + 48f);
            GUI.DrawTexture(panel, Backdrop());

            return new GUILayout.AreaScope(area);
        }

        private Texture2D Backdrop()
        {
            if (_backdrop != null)
                return _backdrop;

            _backdrop = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
            _backdrop.SetPixel(0, 0, new Color(0.04f, 0.04f, 0.05f, 0.82f));
            _backdrop.Apply();

            return _backdrop;
        }

        private GUIStyle TitleStyle() => _titleStyle ??= new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 34,
            fontStyle = FontStyle.Bold,
        };

        private GUIStyle StatusStyle() => _statusStyle ??= new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            wordWrap = true,
        };

        private static void Quit()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }
    }
}
