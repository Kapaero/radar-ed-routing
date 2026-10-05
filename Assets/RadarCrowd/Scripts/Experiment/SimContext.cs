namespace RadarCrowd
{
    /// <summary>Shared references of one run, handed to agents at spawn time.</summary>
    public sealed class SimContext
    {
        public RunConfig Cfg;
        public EdInputs Ed;
        public HospitalLayout Layout;
        public HospitalState Hospital;
        public Router Router;
        public SensorHub Hub;
        public MetricsRecorder Metrics;
        public StaffDispatcher Staff;
        public ExperimentRunner Runner;

        /// <summary>Own random stream per agent, so behaviour stays paired across conditions as far as possible.</summary>
        public SeededRandom StreamFor(string name) => SeededRandom.For(Cfg.seed, name);
    }
}
