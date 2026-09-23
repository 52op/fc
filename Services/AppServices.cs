namespace FC.Services
{
    /// <summary>
    /// App 启动时装配好的共享服务 + 依赖。ViewModel 通过它协作，避免构造函数参数爆炸。
    /// </summary>
    public sealed class AppServices
    {
        public IScanner Scanner { get; private set; }

        public IVerifier Verifier { get; private set; }

        public ICopyMigrator Migrator { get; private set; }

        public IRecordStore Records { get; private set; }

        public IDialogService Dialogs { get; private set; }

        /// <summary>快速复用缓存（仅 Scanner.QuickReuseMode 开启时生效）</summary>
        public ScanCache ScanCache { get; private set; }

        public static AppServices CreateDefault()
        {
            var store = new XmlRecordStore(XmlRecordStore.DefaultPath());
            var scanner = new Scanner();
            var cache = new ScanCache(ScanCache.DefaultPath());
            cache.Load();
            scanner.Cache = cache;

            var verifier = new TreeVerifier();
            var migrator = new CopyMigrator(verifier, store);

            return new AppServices
            {
                Scanner = scanner,
                Verifier = verifier,
                Migrator = migrator,
                Records = store,
                Dialogs = new WpfDialogService(),
                ScanCache = cache
            };
        }
    }
}