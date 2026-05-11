using UnityEngine;
using UnityEditor;
using DungeonRunners.Core;

namespace DungeonRunners
{
    [CustomEditor(typeof(ServerConfig))]
    public class ServerConfigEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            ServerConfig config = (ServerConfig)target;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Dungeon Runners Server Configuration", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            // Auth Server Settings
            EditorGUILayout.LabelField("Auth Server Settings", EditorStyles.boldLabel);
            config.authServerIP = EditorGUILayout.TextField("IP Address", config.authServerIP);
            config.authServerPort = EditorGUILayout.IntField("Port", config.authServerPort);
            EditorGUILayout.Space();

            // Game Server Settings
            EditorGUILayout.LabelField("Game Server Settings", EditorStyles.boldLabel);
            config.gameServerIP = EditorGUILayout.TextField("IP Address", config.gameServerIP);
            config.gameServerPort = EditorGUILayout.IntField("Port", config.gameServerPort);
            config.gameServerName = EditorGUILayout.TextField("Server Name", config.gameServerName);
            EditorGUILayout.Space();

            // Encryption Keys
            EditorGUILayout.LabelField("Encryption Keys", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("These keys are pre-configured for Dungeon Runners compatibility. Do not change unless you know what you're doing.", MessageType.Info);
            config.blowfishKey = EditorGUILayout.TextField("Blowfish Key", config.blowfishKey);
            config.desKey = EditorGUILayout.TextField("DES Key", config.desKey);
            EditorGUILayout.Space();

            // Server Info
            EditorGUILayout.LabelField("Server Information", EditorStyles.boldLabel);
            config.maxPlayers = EditorGUILayout.IntField("Max Players", config.maxPlayers);
            config.serverVersion = EditorGUILayout.TextField("Version", config.serverVersion);
            config.enableDebugLogging = EditorGUILayout.Toggle("Debug Logging", config.enableDebugLogging);
            EditorGUILayout.Space();

            // World Settings
            EditorGUILayout.LabelField("World Settings", EditorStyles.boldLabel);
            config.defaultWorldId = EditorGUILayout.IntField("Default World ID", config.defaultWorldId);
            config.defaultSpawnPosition = EditorGUILayout.Vector3Field("Spawn Position", config.defaultSpawnPosition);
            config.defaultZoneId = EditorGUILayout.IntField("Default Zone ID", config.defaultZoneId);
            EditorGUILayout.Space();

            // Quick Actions
            EditorGUILayout.LabelField("Quick Actions", EditorStyles.boldLabel);
            if (GUILayout.Button("Reset to Defaults"))
            {
                if (EditorUtility.DisplayDialog("Reset Configuration", 
                    "Are you sure you want to reset all settings to defaults?", "Yes", "No"))
                {
                    ResetToDefaults(config);
                }
            }

            if (GUI.changed)
            {
                EditorUtility.SetDirty(config);
            }
        }

        private void ResetToDefaults(ServerConfig config)
        {
            config.authServerIP = "0.0.0.0";
            config.authServerPort = 2110;
            config.gameServerIP = "0.0.0.0";
            config.gameServerPort = 2603;
            config.gameServerName = "Dungeon Runners Server";
            config.blowfishKey = "[;'.]94-31==-%&@!^+]";
            config.desKey = "TEST";
            config.maxPlayers = 100;
            config.serverVersion = "1.0.0";
            config.enableDebugLogging = true;
            config.defaultWorldId = 1;
            config.defaultSpawnPosition = new Vector3(100, 0, 100);
            config.defaultZoneId = 1;
            
            EditorUtility.SetDirty(config);
        }
    }
}