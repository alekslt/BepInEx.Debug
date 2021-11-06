using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using Mono.Cecil;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Common;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ScriptEngine
{
    [BepInPlugin(GUID, "Script Engine", Version)]
    public class ScriptEngine : BaseUnityPlugin
    {
        private static readonly string BepInExAssemblyName = Assembly.GetAssembly(typeof(TypeLoader)).GetName().Name;
        public const string GUID = "com.bepis.bepinex.scriptengine";
        public const string Version = Metadata.Version;

        public string ScriptDirectory => Path.Combine(Paths.BepInExRootPath, "scripts");

        GameObject scriptManager;

        ConfigEntry<bool> LoadOnStart { get; set; }
        ConfigEntry<Key> ReloadKey { get; set; }

        static ScriptEngine instance;

        bool IsKeyboardShortcutPressed(Key key)
        {
            return Keyboard.current[key].wasPressedThisFrame;
        }

        void Awake()
        {
            instance = this;

            LoadOnStart = Config.Bind("General", "LoadOnStart", false, new ConfigDescription("Load all plugins from the scripts folder when starting the application"));
            ReloadKey = Config.Bind("General", "ReloadKey", Key.F6, new ConfigDescription("Press this key to reload all the plugins from the scripts folder"));

            if (LoadOnStart.Value)
            {
                UnloadPlugins();
                ReloadPlugins();
            } 
        }

        void Update()
        {
            if (IsKeyboardShortcutPressed(ReloadKey.Value))
            {
                UnloadPlugins();
                ReloadPlugins();
            }
        }

        private static bool HasBepinPlugins(AssemblyDefinition ass)
        {
            if (ass.MainModule.AssemblyReferences.All(r => r.Name != BepInExAssemblyName)
                || ass.MainModule.GetTypeReferences().All(r => r.FullName != "BepInEx.BaseUnityPlugin"))
            {
                return false;
            }
            return true;
        }

        public static List<string> FindPluginTypes(string searchDirectory, Func<AssemblyDefinition, bool> assemblyFilter = null)
        {
            var result = new List<string>();
            var SearchFolders = new List<string>() { searchDirectory }; SearchFolders.AddRange(Directory.GetDirectories(searchDirectory));
            IEnumerable<string> dlls = SearchFolders.SelectMany(directory => Directory.GetFiles(directory, "*.dll"));

            foreach (string dll in dlls)
            {
                //ScriptEngine.instance.Logger.LogDebug($"Trying loading {dll}");
                try
                {
                    var ass = AssemblyDefinition.ReadAssembly(dll);
                    if (!assemblyFilter?.Invoke(ass) ?? false)
                    {
                        //ScriptEngine.instance.Logger.LogDebug($"Filter not matching. Disposing {dll}");
                        ass.Dispose();
                        continue;
                    }
                    //ScriptEngine.instance.Logger.LogDebug($"Filter matching. Adding {dll}");
                    result.Add(dll);
                    ass.Dispose();
                }
                catch (BadImageFormatException e)
                {
                    //ScriptEngine.instance.Logger.LogDebug($"Skipping loading {dll} because it's not a valid .NET assembly. Full error: {e.Message}");
                }
                catch (Exception e)
                {
                    //ScriptEngine.instance.Logger.LogError(e.ToString());
                }
            }
            return result;
        }


        void UnloadPlugins()
        {
            Logger.Log(LogLevel.Info, "Unloading old plugin instances");
            Destroy(scriptManager);
            scriptManager = new GameObject($"ScriptEngine_{DateTime.Now.Ticks}");
            DontDestroyOnLoad(scriptManager);
        }
        void ReloadPlugins()
        {
            Logger.Log(LogLevel.Info, "Looking for plugins in " + ScriptDirectory);
            var files = FindPluginTypes(ScriptDirectory, HasBepinPlugins); //Directory.GetFiles(ScriptDirectory, "*.dll");
            //Logger.Log(LogLevel.Info, $"Matching files: {files.Count}");
            if (files.Count > 0)
            {
                foreach (string path in files)
                {
                    LoadDLL(path, scriptManager);
                }

                Logger.LogMessage("Reloaded all plugins!");
            }
            else
            {
                Logger.LogMessage("No plugins to reload");
            }
        }

        void LoadDLL(string path, GameObject obj)
        {
            Logger.Log(LogLevel.Info, $"Loading plugins from {path}");

            var defaultResolver = new DefaultAssemblyResolver();
            defaultResolver.AddSearchDirectory(Path.GetDirectoryName(path));
            defaultResolver.AddSearchDirectory(ScriptDirectory);
            defaultResolver.AddSearchDirectory(Paths.ManagedPath);
            defaultResolver.AddSearchDirectory(Paths.BepInExAssemblyDirectory);
            
            using (var dll = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { AssemblyResolver = defaultResolver }))
            {
                dll.Name.Name = $"{dll.Name.Name}-{DateTime.Now.Ticks}";

                using (var ms = new MemoryStream())
                {
                    dll.Write(ms);
                    var ass = Assembly.Load(ms.ToArray());

                    foreach (Type type in GetTypesSafe(ass))
                    {
                        try
                        {
                            if (typeof(BaseUnityPlugin).IsAssignableFrom(type))
                            {
                                var metadata = MetadataHelper.GetMetadata(type);
                                if (metadata != null)
                                {
                                    var typeDefinition = dll.MainModule.Types.First(x => x.FullName == type.FullName);
                                    var typeInfo = Chainloader.ToPluginInfo(typeDefinition);
                                    Chainloader.PluginInfos[metadata.GUID] = typeInfo;

                                    Logger.Log(LogLevel.Info, $"Loading {metadata.GUID}");
                                    StartCoroutine(DelayAction(() =>
                                    {
                                        try
                                        {
                                            obj.AddComponent(type);
                                        }
                                        catch (Exception e)
                                        {
                                            Logger.LogError($"Failed to load plugin {metadata.GUID} because of exception: {e}");
                                        }
                                    }));
                                }
                            }
                        }
                        catch (Exception e)
                        {
                            Logger.LogError($"Failed to load plugin {type.Name} because of exception: {e}");
                        }
                    }
                }
            }
        }

        private IEnumerable<Type> GetTypesSafe(Assembly ass)
        {
            try
            {
                return ass.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                var sbMessage = new StringBuilder();
                sbMessage.AppendLine("\r\n-- LoaderExceptions --");
                foreach (var l in ex.LoaderExceptions)
                    sbMessage.AppendLine(l.ToString());
                sbMessage.AppendLine("\r\n-- StackTrace --");
                sbMessage.AppendLine(ex.StackTrace);
                Logger.LogError(sbMessage.ToString());
                return ex.Types.Where(x => x != null);
            }
        }

        IEnumerator DelayAction(Action action)
        {
            yield return null;
            action();
        }
    }
}
