using Colossal.UI.Binding;
using Game.UI.InGame;
using Unity.Entities;

namespace CS2DevTools
{
    // A small section in the game's selected-object info panel with an "Inspect in Dev Tools" button.
    // The panel's UI module renders it (selectedInfoSectionComponents["CS2DevTools.InspectSection"]).
    public sealed partial class InspectSection : InfoSectionBase
    {
        string path = "", label = "";

        protected override string group => "CS2DevTools.InspectSection";
        protected override bool displayForDestroyedObjects => true;
        protected override bool displayForOutsideConnections => true;
        protected override bool displayForUnderConstruction => true;
        protected override bool displayForUpgrades => true;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_InfoUISystem.AddMiddleSection(this);
        }

        protected override void OnUpdate()
        {
            base.OnUpdate();
            visible = selectedEntity != Entity.Null;
        }

        protected override void Reset() { path = ""; label = ""; }

        protected override void OnProcess()
        {
            var e = selectedEntity;
            path = "#" + e.Index + ":" + e.Version;
            label = DevToolsUISystem.Instance?.DescribeEntity(e) ?? "";
        }

        public override void OnWriteProperties(IJsonWriter writer)
        {
            writer.PropertyName("path"); writer.Write(path);
            writer.PropertyName("label"); writer.Write(label);
        }
    }
}
