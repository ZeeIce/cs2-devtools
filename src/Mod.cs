using Colossal.Logging;
using Game;
using Game.Modding;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("CS2DevTools.Tests")]

namespace CS2DevTools
{
    public sealed class Mod : IMod
    {
        internal static readonly ILog Log = LogManager.GetLogger(nameof(CS2DevTools)).SetShowsErrorsInUI(false);

        public void OnLoad(UpdateSystem updateSystem)
        {
            // UIUpdate runs every frame, paused or not, so pins hold while the city is paused.
            updateSystem.UpdateAt<DevToolsUISystem>(SystemUpdatePhase.UIUpdate);
            Log.Info("Dev Tools loaded");
        }

        public void OnDispose() { }
    }
}
