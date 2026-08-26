using System.Text.Json.Serialization;

namespace Leaves
{
    // The suite-wide HTTP-route directory. Every Leaves mod hands its public routes to the shared Utils
    // singleton during its own OnLoad; the Dev page in the web UI (served at /webui/dev) reads them all back
    // through WebUiHost's /webui/dev/routes endpoint. Because a mod only registers when it's actually loaded,
    // the list is self-correcting: an uninstalled mod simply never shows up.
    //
    // The list is hand-authored per mod (there's no reflection over each listener's path matching), so it can
    // drift if a route is added without updating the registration - keep the descriptions one line and update
    // them alongside the route.
    public partial class Utils
    {
        // Registration happens on the load thread(s); reads happen at runtime from the web UI. All access is
        // locked so a mid-load query can't see a torn list.
        private readonly object routeRegistryLock = new();
        private readonly List<RouteInfo> registeredRoutes = new();

        // Called by each mod (usually from OnLoad) with the routes it serves. modName is stamped onto every
        // entry here so callers don't repeat it. Idempotent: any earlier entries for the same mod are dropped
        // first, so a second call (or a re-registration) replaces rather than duplicates.
        public void RegisterRoutes(string modName, IEnumerable<RouteInfo> routes)
        {
            lock (routeRegistryLock)
            {
                registeredRoutes.RemoveAll(delegate (RouteInfo existing) { return existing.Mod == modName; });
                foreach (var route in routes)
                {
                    route.Mod = modName;
                    registeredRoutes.Add(route);
                }
            }
        }

        // A snapshot copy of everything registered so far, for the Dev page to serialize. A copy so the caller
        // can't hold the lock while serializing (and can't mutate our backing list).
        public List<RouteInfo> GetRegisteredRoutes()
        {
            lock (routeRegistryLock)
            {
                return new List<RouteInfo>(registeredRoutes);
            }
        }
    }

    // One public HTTP route a mod exposes, as shown on the Dev page. Mod is filled in by RegisterRoutes.
    // Kind groups routes for the reader: "ui" = an HTML page, "api" = a data endpoint meant to be called by
    // hand or by a page, "internal" = a route the client talks to but you'd rarely poke directly.
    // Danger marks a route that can permanently alter data when called - the Dev page flags those with a
    // warning symbol so nobody fires one off casually.
    public class RouteInfo
    {
        [JsonPropertyName("mod")] public string Mod { get; set; } = "";
        [JsonPropertyName("method")] public string Method { get; set; } = "GET";
        [JsonPropertyName("path")] public string Path { get; set; } = "";
        [JsonPropertyName("description")] public string Description { get; set; } = "";
        [JsonPropertyName("kind")] public string Kind { get; set; } = "api";
        [JsonPropertyName("danger")] public bool Danger { get; set; }

        public RouteInfo(
            string method,
            string path,
            string description,
            string kind)
        {
            Method = method;
            Path = path;
            Description = description;
            Kind = kind;
        }

        // Overload for a route that mutates data - pass danger: true so the Dev page warns before it's called.
        public RouteInfo(
            string method,
            string path,
            string description,
            string kind,
            bool danger)
        {
            Method = method;
            Path = path;
            Description = description;
            Kind = kind;
            Danger = danger;
        }
    }
}
