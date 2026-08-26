using SPTarkov.Server.Core.Models.Common;

namespace Leaves
{
    // Thread-safe name->MongoId store backed by an "ids.jsonc" file, so generated content keeps a stable id across restarts.
    // A key always resolves to the same MongoId, or creates a new permanent one.
    public class IdStore
    {
        private readonly string modPath;
        private readonly string relativePath;
        private readonly Dictionary<string, MongoId> ids;

        // Guards GetOrCreate and Save against concurrent profile generations racing the dictionary and its file.
        private readonly object gate = new();

        public IdStore(string modPath, string relativePath)
        {
            this.modPath = modPath;
            this.relativePath = relativePath;

            string fullPath = System.IO.Path.Combine(modPath, relativePath);
            if (System.IO.File.Exists(fullPath))
            {
                this.ids = Leaves.Utils.Instance.loadFile<Dictionary<string, MongoId>>(fullPath);
            }
            else
            {
                this.ids = new Dictionary<string, MongoId>();
            }
        }

        public MongoId GetOrCreate(string key)
        {
            lock (gate)
            {
                if (!ids.TryGetValue(key, out var id))
                {
                    id = new MongoId();
                    ids[key] = id;
                }
                return id;
            }
        }

        public void Save()
        {
            lock (gate)
            {
                Leaves.Utils.Instance.saveJsonFile(ids, modPath, relativePath);
            }
        }

        public HashSet<MongoId> ValuesSnapshot()
        {
            lock (gate)
            {
                return new HashSet<MongoId>(ids.Values);
            }
        }
    }
}
