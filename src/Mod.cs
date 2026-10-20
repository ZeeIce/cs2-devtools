using Colossal.Logging;
using Game;
using Game.Modding;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("PresidenteDevTools.Tests")]

namespace PresidenteDevTools
{
    public sealed class Mod : IMod
    {
        internal static readonly ILog Log = LogManager.GetLogger(nameof(PresidenteDevTools)).SetShowsErrorsInUI(false);

        public void OnLoad(UpdateSystem updateSystem)
        {
            // UIUpdate runs every frame, paused or not, so pins hold while the city is paused.
            updateSystem.UpdateAt<DevToolsUISystem>(SystemUpdatePhase.UIUpdate);
            Log.Info("Presidente Dev Tools loaded");
        }

        public void OnDispose() { }
    }
}
