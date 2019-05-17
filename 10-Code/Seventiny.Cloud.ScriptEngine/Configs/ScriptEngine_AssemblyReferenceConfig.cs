﻿using SevenTiny.Bantina.Configuration;
using System.Collections.Generic;
using System.Linq;

namespace Seventiny.Cloud.ScriptEngine.Configs
{
    [ConfigName("ScriptEngine_AssemblyReference")]
    internal class ScriptEngine_AssemblyReferenceConfig : MySqlRowConfigBase<ScriptEngine_AssemblyReferenceConfig>
    {
        public static ScriptEngine_AssemblyReferenceConfig Instance = new ScriptEngine_AssemblyReferenceConfig();
        protected override string _ConnectionString => GetConnectionStringFromAppSettings("SevenTinyConfig");

        [ConfigProperty]
        public string AppName { get; set; }
        [ConfigProperty]
        public string Assembly { get; set; }

        public static List<string> GetByAppName(string appName)
        {
            return Instance.Config?.Where(t => t.AppName.Equals(appName)).Select(t => t.Assembly).ToList();
        }
    }
}