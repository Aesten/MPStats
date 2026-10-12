using System;
using System.IO;

namespace MPStats;

// Our names for maps: the name typed in commands (!setmap xauna:winter), from the maps file MPManager uses
// (MPM_MAP_NAMES_FILE, set by the launcher: ~/bannerlord/maps.txt). Lines are "scene id | display name | aliases |
// short aliases"; the first alias is the map's name. Read at each match start (a small file, once per match).
// Without the file, or for a scene it doesn't list, the scene id is used.
internal static class MapNames
{
    public static string Name(string sceneId)
    {
        try
        {
            string file = Environment.GetEnvironmentVariable("MPM_MAP_NAMES_FILE");
            if (string.IsNullOrEmpty(file) || !File.Exists(file))
            {
                return sceneId;
            }
            foreach (string raw in File.ReadAllLines(file))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#"))
                {
                    continue;
                }
                string[] columns = line.Split('|');
                if (columns.Length < 3 || !columns[0].Trim().Equals(sceneId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                string[] aliases = columns[2].Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                return aliases.Length > 0 ? aliases[0].ToLowerInvariant() : sceneId;
            }
        }
        catch (Exception e)
        {
            MPStatsSubModule.Log("Could not read map names: " + e.Message);
        }
        return sceneId;
    }
}
