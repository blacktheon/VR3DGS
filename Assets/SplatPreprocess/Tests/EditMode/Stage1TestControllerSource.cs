using Oculus.Interaction.Input;

namespace SplatPreprocess.Tests
{
    // Hardware input is supplied as recorded Meta data; the real Controller and review code consume it.
    public sealed class Stage1TestControllerSource : IDataSource<ControllerDataAsset>
    {
        public readonly ControllerDataAsset Data = new ControllerDataAsset
        {
            IsDataValid = true,
            IsConnected = true,
            Config = new ControllerDataSourceConfig { Handedness = Handedness.Right }
        };
        public int CurrentDataVersion { get; private set; }
        public event System.Action InputDataAvailable = delegate { };
        public ControllerDataAsset GetData() => Data;
        public void MarkInputDataRequiresUpdate() { CurrentDataVersion++; InputDataAvailable(); }
    }
}
