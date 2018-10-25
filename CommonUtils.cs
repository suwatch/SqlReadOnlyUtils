using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SqlReadOnlyUtils
{
    public static class CommonUtils
    {
        const string LockboxConfigsPath = @"\\reddog\builds\branches\rd_websites_stable_latest_amd64fre\bin\hosting\Azure\LockboxConfigs\Prod";

        public static IEnumerable<string> GetGeoRegions()
        {
            string cacheFile = Environment.ExpandEnvironmentVariables(@"%USERPROFILE%\SqlReadOnlyUtils\regions.txt");

            try
            {
                if (File.Exists(cacheFile) && File.GetLastWriteTimeUtc(cacheFile) > DateTime.UtcNow.AddHours(-6))
                {
                    return File.ReadLines(cacheFile);
                }

                var regions = Directory.GetDirectories(LockboxConfigsPath)
                    .Select(n => Path.GetFileName(n).ToLowerInvariant())
                    .Where(n => n.StartsWith("gr-prod-", StringComparison.OrdinalIgnoreCase) && n.Split('-').Length == 3);

                File.WriteAllLines(cacheFile, regions.ToArray());
                return regions;
            }
            catch (IOException ex)
            {
                Console.WriteLine(ex);
                if (File.Exists(cacheFile))
                {
                    return File.ReadLines(cacheFile);
                }

                throw;
            }
        }

        public static IEnumerable<string> GetStamps(string region)
        {
            string cacheFile = Environment.ExpandEnvironmentVariables(@"%USERPROFILE%\SqlReadOnlyUtils\stamps_" + region + ".txt");

            try
            {
                if (File.Exists(cacheFile) && File.GetLastWriteTimeUtc(cacheFile) > DateTime.UtcNow.AddHours(-6))
                {
                    return File.ReadLines(cacheFile);
                }

                IEnumerable<string> stamps;
                if (region == "all")
                {
                    stamps = Directory.GetDirectories(LockboxConfigsPath)
                        .Select(st => Path.GetFileName(st).ToLowerInvariant())
                        .Where(st => st.StartsWith("waws-prod-", StringComparison.OrdinalIgnoreCase) && st.Split('-').Length == 4)
                        .Where(st =>
                        {
                            var n = st.Split('-').Last();
                            return n.Length == 3 && n[0] >= '0' && n[0] <= '9' && n[2] >= '0' && n[2] <= '9';
                        });
                }
                else
                {
                    stamps = Directory.GetDirectories(LockboxConfigsPath)
                        .Select(st => Path.GetFileName(st).ToLowerInvariant())
                        .Where(st => st.StartsWith("waws-prod-" + region + "-", StringComparison.OrdinalIgnoreCase) && st.Split('-').Length == 4)
                        .Where(st =>
                        {
                            var n = st.Split('-').Last();
                            return n.Length == 3 && n[0] >= '0' && n[0] <= '9' && n[2] >= '0' && n[2] <= '9';
                        });

                    if (region == "kw1")
                    {
                        stamps = stamps.Concat(Directory.GetDirectories(LockboxConfigsPath)
                            .Select(st => Path.GetFileName(st).ToLowerInvariant())
                            .Where(st => st.StartsWith("waws-prod-ty1-", StringComparison.OrdinalIgnoreCase) && st.Split('-').Length == 4)
                            .Where(st =>
                            {
                                var n = st.Split('-').Last();
                                return n.Length == 3 && n[0] >= '0' && n[0] <= '9' && n[2] >= '0' && n[2] <= '9';
                            }));
                    }
                }

                File.WriteAllLines(cacheFile, stamps.ToArray());
                return stamps;
            }
            catch (IOException ex)
            {
                Console.WriteLine(ex);
                if (File.Exists(cacheFile))
                {
                    return File.ReadLines(cacheFile);
                }

                throw;
            }
        }
    }
}
