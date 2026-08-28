using UnityEngine;
using UnityEngine.Rendering;

namespace ChopChop.UI
{
    /// <summary>
    /// The canvas the whole game draws on, and the one object that decides whether there
    /// should be one at all.
    ///
    /// Lives in the boot scene next to the composition root and survives the world scene
    /// load the same way it does — <see cref="Object.DontDestroyOnLoad"/>. The world is
    /// loaded with <c>ReplaceOption.All</c>, so anything that has to outlive boot has to
    /// say so itself (TECH 8.1a).
    ///
    /// **A dedicated server must never build this.** A canvas, an EventSystem and a font
    /// atlas on a headless box are wasted at best and a null graphics device dereference
    /// at worst, and TECH 15 says the headless run is the only test that catches
    /// editor-only assumptions. Hence the check below rather than trust that nobody
    /// drags this into a server scene.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class UiRoot : MonoBehaviour
    {
        private void Awake()
        {
            /* Not asked of the bootstrap, which the UI is forbidden from referencing
             * (TECH 3). These two are the honest question anyway: the role decides
             * whether a *server* runs, and what matters here is whether anything can be
             * drawn at all. -batchmode -nographics answers both. */
            if (Application.isBatchMode || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                Destroy(gameObject);
                return;
            }

            DontDestroyOnLoad(gameObject);
        }
    }
}
