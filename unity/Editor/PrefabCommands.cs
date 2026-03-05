// Place in Assets/Editor/PrefabCommands.cs
// Requires TextMeshPro package (com.unity.textmeshpro)

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace UnityMCP.Editor
{
public static class PrefabCommands
{
    static GameObject _openPrefabRoot;
    static string _openPrefabPath;
    static bool _openPrefabDirty;

    static readonly HashSet<string> MutatingActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "create_game_object",
        "add_component",
        "set_rect_transform",
        "set_image",
        "set_text_mesh_pro",
        "set_object_reference",
    };

    static readonly HashSet<string> LifecycleActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "open_prefab",
        "save_prefab",
        "close_prefab",
    };

    public static object ExecuteActions(
        JArray actions,
        bool transactional,
        bool rollbackOnFailure,
        bool preview = false)
    {
        if (actions == null || actions.Count == 0)
            throw new Exception("actions array required");

        if (preview)
            transactional = true;

        var actionResults = new JArray();
        var plannedChanges = preview ? DescribePlannedChanges(actions) : null;
        var backups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool rolledBack = false;
        string rollbackError = null;

        try
        {
            for (int i = 0; i < actions.Count; i++)
            {
                JObject action = actions[i] as JObject
                                 ?? throw new Exception($"actions[{i}] must be an object");
                string name = action["action"]?.ToString() ?? "";

                try
                {
                    if (transactional)
                        EnsureBackupForAction(name, action, backups);

                    object result = ExecuteAction(action);
                    actionResults.Add(new JObject
                    {
                        ["index"] = i,
                        ["action"] = name,
                        ["success"] = true,
                        ["result"] = result != null ? JToken.FromObject(result) : new JObject(),
                    });
                }
                catch (Exception ex)
                {
                    actionResults.Add(new JObject
                    {
                        ["index"] = i,
                        ["action"] = name,
                        ["success"] = false,
                        ["error"] = ex.Message,
                        ["error_type"] = ex.GetType().FullName,
                    });
                    throw new Exception($"Action #{i} ('{name}') failed: {ex.Message}", ex);
                }
            }

            if (transactional && _openPrefabRoot != null && _openPrefabDirty)
                SaveCurrentPrefab();

            if (preview)
            {
                var prefabChanges = DescribePrefabFileChanges(backups);
                Rollback(backups);
                rolledBack = true;
                return new
                {
                    success = true,
                    preview = true,
                    rolled_back = true,
                    transactional,
                    action_count = actions.Count,
                    action_results = actionResults,
                    planned_changes = plannedChanges,
                    prefab_changes = prefabChanges,
                };
            }

            CleanupBackups(backups);
            return new
            {
                success = true,
                transactional,
                action_count = actions.Count,
                action_results = actionResults,
            };
        }
        catch (Exception ex)
        {
            if (transactional && rollbackOnFailure)
            {
                try
                {
                    Rollback(backups);
                    rolledBack = true;
                }
                catch (Exception rollbackEx)
                {
                    rollbackError = rollbackEx.ToString();
                }
            }

            return new
            {
                success = false,
                error = ex.Message,
                error_type = ex.GetType().FullName,
                preview,
                rolled_back = rolledBack,
                rollback_error = rollbackError,
                transactional,
                action_results = actionResults,
                planned_changes = plannedChanges,
            };
        }
        finally
        {
            if (!transactional || !rolledBack)
                CleanupBackups(backups);
        }
    }

    public static object InspectPrefab(JObject req)
    {
        string prefabPath = RequiredString(req, "prefab_path");
        string targetPath = req["target_path"]?.ToString();
        bool includeValues = req["include_serialized_values"]?.Value<bool>() ?? false;
        int maxProps = Mathf.Clamp(req["max_properties_per_component"]?.Value<int>() ?? 512, 1, 4000);

        return WithPrefabRoot(prefabPath, root =>
        {
            GameObject start = string.IsNullOrWhiteSpace(targetPath)
                ? root
                : FindGameObjectByPath(root, targetPath)
                  ?? throw new Exception($"Could not find target_path: {targetPath}");

            var nodes = new JArray();
            CollectNode(start.transform, root.transform, nodes, includeValues, maxProps);

            return new
            {
                success = true,
                prefab_path = prefabPath,
                inspected_path = GetGameObjectPath(start, root),
                node_count = nodes.Count,
                nodes,
            };
        });
    }

    public static object GetObjectReference(JObject req)
    {
        string prefabPath = RequiredString(req, "prefab_path");
        string targetPath = RequiredString(req, "target_path");
        string propertyPath = RequiredString(req, "property_path");
        string componentType = req["component_type"]?.ToString();
        int? componentIndex = req["component_index"]?.Value<int?>();

        var info = ReadObjectReference(prefabPath, targetPath, propertyPath, componentType, componentIndex);
        return new
        {
            success = true,
            prefab_path = prefabPath,
            target_path = targetPath,
            component_type = info.ComponentType,
            property_path = propertyPath,
            value = info.Reference,
        };
    }

    public static object AssertObjectReference(JObject req)
    {
        string prefabPath = RequiredString(req, "prefab_path");
        string targetPath = RequiredString(req, "target_path");
        string propertyPath = RequiredString(req, "property_path");
        string componentType = req["component_type"]?.ToString();
        int? componentIndex = req["component_index"]?.Value<int?>();
        bool failOnMismatch = req["fail_on_mismatch"]?.Value<bool>() ?? true;

        var info = ReadObjectReference(prefabPath, targetPath, propertyPath, componentType, componentIndex);
        var mismatches = new List<string>();

        if (req.TryGetValue("expected_null", out var expectedNullToken))
        {
            bool expectedNull = expectedNullToken.Value<bool>();
            if (expectedNull != info.IsNull)
                mismatches.Add($"expected_null={expectedNull} but actual_is_null={info.IsNull}");
        }

        string expectedAssetPath = req["expected_asset_path"]?.ToString();
        if (!string.IsNullOrWhiteSpace(expectedAssetPath))
        {
            string actualAssetPath = info.Reference["asset_path"]?.ToString();
            if (!AssetPathsEqual(expectedAssetPath, actualAssetPath))
                mismatches.Add($"expected_asset_path='{expectedAssetPath}' but actual_asset_path='{actualAssetPath}'");
        }

        string expectedTargetPath = req["expected_target_path"]?.ToString();
        if (!string.IsNullOrWhiteSpace(expectedTargetPath))
        {
            string actualTargetPath = info.Reference["target_path"]?.ToString();
            if (!string.Equals(expectedTargetPath, actualTargetPath, StringComparison.Ordinal))
                mismatches.Add($"expected_target_path='{expectedTargetPath}' but actual_target_path='{actualTargetPath}'");
        }

        string expectedComponentType = req["expected_component_type"]?.ToString();
        if (!string.IsNullOrWhiteSpace(expectedComponentType))
        {
            string actualComponentType = info.Reference["component_type"]?.ToString();
            if (!string.Equals(expectedComponentType, actualComponentType, StringComparison.Ordinal))
                mismatches.Add($"expected_component_type='{expectedComponentType}' but actual_component_type='{actualComponentType}'");
        }

        bool matched = mismatches.Count == 0;
        if (!matched && failOnMismatch)
        {
            return new
            {
                success = false,
                matched = false,
                error = "Object reference assertion failed.",
                mismatches,
                actual = info.Reference,
                component_type = info.ComponentType,
                property_path = propertyPath,
            };
        }

        return new
        {
            success = true,
            matched,
            mismatches,
            actual = info.Reference,
            component_type = info.ComponentType,
            property_path = propertyPath,
        };
    }

    public static object SearchPrefabs(JObject req)
    {
        string rootFolder = req["root_folder"]?.ToString();
        if (string.IsNullOrWhiteSpace(rootFolder))
            rootFolder = "Assets";

        string nameContains = req["name_contains"]?.ToString();
        string componentTypeName = req["component_type"]?.ToString();
        string basePrefabPath = req["base_prefab_path"]?.ToString();
        string referencesAssetPath = req["references_asset_path"]?.ToString();
        bool includeChildren = req["include_children"]?.Value<bool>() ?? true;
        int limit = Mathf.Clamp(req["limit"]?.Value<int>() ?? 200, 1, 5000);

        Type componentType = null;
        if (!string.IsNullOrWhiteSpace(componentTypeName))
            componentType = ResolveComponentType(componentTypeName);

        string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { rootFolder });
        var matches = new JArray();

        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);

            if (!string.IsNullOrWhiteSpace(nameContains))
            {
                string name = Path.GetFileNameWithoutExtension(path);
                if (name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
            }

            if (!string.IsNullOrWhiteSpace(basePrefabPath) && !InheritsFromPrefab(path, basePrefabPath))
                continue;

            if (!string.IsNullOrWhiteSpace(referencesAssetPath) && !DependsOnAsset(path, referencesAssetPath))
                continue;

            if (componentType != null && !PrefabHasComponent(path, componentType, includeChildren))
                continue;

            matches.Add(new JObject { ["prefab_path"] = path });
            if (matches.Count >= limit)
                break;
        }

        return new
        {
            success = true,
            root_folder = rootFolder,
            total_scanned = guids.Length,
            result_count = matches.Count,
            prefabs = matches,
        };
    }

    public static object DescribeActions()
    {
        return new
        {
            success = true,
            supported_commands = new[]
            {
                "ping",
                "play",
                "stop",
                "get_console_errors",
                "execute_actions",
                "preview_actions",
                "inspect_prefab",
                "get_object_reference",
                "assert_object_reference",
                "search_prefabs",
                "execute_actions_on_prefab_variants",
                "execute_menu_item",
                "describe_actions",
            },
            execute_actions = new
            {
                optional = new[] { "transactional", "rollback_on_failure", "preview", "timeout_ms" },
                actions = new[]
                {
                    new
                    {
                        name = "open_prefab",
                        required = new[] { "prefab_path" },
                        optional = Array.Empty<string>(),
                    },
                    new
                    {
                        name = "save_prefab",
                        required = Array.Empty<string>(),
                        optional = Array.Empty<string>(),
                    },
                    new
                    {
                        name = "close_prefab",
                        required = Array.Empty<string>(),
                        optional = new[] { "save" },
                    },
                    new
                    {
                        name = "create_game_object",
                        required = new[] { "name" },
                        optional = new[] { "parent_path", "rect_transform", "as_first_sibling", "as_last_sibling" },
                    },
                    new
                    {
                        name = "add_component",
                        required = new[] { "target_path", "component_type" },
                        optional = new[] { "allow_duplicate" },
                    },
                    new
                    {
                        name = "set_rect_transform",
                        required = new[] { "target_path" },
                        optional = new[]
                        {
                            "anchor_min",
                            "anchor_max",
                            "pivot",
                            "anchored_position",
                            "size_delta",
                            "offset_min",
                            "offset_max",
                            "rotation",
                            "local_scale",
                        },
                    },
                    new
                    {
                        name = "set_image",
                        required = new[] { "target_path" },
                        optional = new[] { "color", "raycast_target", "sprite_path" },
                    },
                    new
                    {
                        name = "set_text_mesh_pro",
                        required = new[] { "target_path" },
                        optional = new[]
                        {
                            "text",
                            "font_size",
                            "alignment",
                            "font_style",
                            "color",
                            "raycast_target",
                        },
                    },
                    new
                    {
                        name = "set_object_reference",
                        required = new[] { "target_path", "property_path" },
                        optional = new[]
                        {
                            "component_type",
                            "component_index",
                            "clear",
                            "reference_asset_path",
                            "reference_target_path",
                            "reference_component_type",
                            "reference_component_index",
                        },
                    },
                },
            },
            execute_actions_on_prefab_variants = new
            {
                required = new[] { "base_prefab_path", "actions" },
                optional = new[]
                {
                    "root_folder",
                    "include_base",
                    "limit",
                    "transactional",
                    "rollback_on_failure",
                    "preview",
                    "stop_on_failure",
                },
                notes = new[]
                {
                    "Each variant batch is wrapped with open_prefab + save_prefab + close_prefab automatically.",
                    "Lifecycle actions (open_prefab/save_prefab/close_prefab) are not allowed inside actions.",
                },
            },
        };
    }

    public static object ExecuteActionsOnPrefabVariants(JObject req)
    {
        string basePrefabPath = RequiredString(req, "base_prefab_path");
        string rootFolder = req["root_folder"]?.ToString();
        if (string.IsNullOrWhiteSpace(rootFolder))
            rootFolder = "Assets";

        bool includeBase = req["include_base"]?.Value<bool>() ?? false;
        int limit = Mathf.Clamp(req["limit"]?.Value<int>() ?? 200, 1, 5000);
        bool transactional = req["transactional"]?.Value<bool>() ?? true;
        bool rollbackOnFailure = req["rollback_on_failure"]?.Value<bool>() ?? true;
        bool preview = req["preview"]?.Value<bool>() ?? false;
        bool stopOnFailure = req["stop_on_failure"]?.Value<bool>() ?? false;
        var actions = req["actions"] as JArray;
        if (actions == null || actions.Count == 0)
            throw new Exception("actions array required");

        string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { rootFolder });
        var targetPaths = new List<string>();
        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (!InheritsFromPrefab(path, basePrefabPath))
                continue;
            if (!includeBase && AssetPathsEqual(path, basePrefabPath))
                continue;

            targetPaths.Add(path);
            if (targetPaths.Count >= limit)
                break;
        }

        var results = new JArray();
        bool allSucceeded = true;

        foreach (string targetPath in targetPaths)
        {
            JArray batch = BuildVariantBatchActions(targetPath, actions);
            JObject result = JObject.FromObject(ExecuteActions(batch, transactional, rollbackOnFailure, preview));
            bool success = result["success"]?.Value<bool>() ?? false;
            allSucceeded &= success;
            result["prefab_path"] = targetPath;
            results.Add(result);

            if (!success && stopOnFailure)
                break;
        }

        return new
        {
            success = allSucceeded,
            preview,
            base_prefab_path = basePrefabPath,
            root_folder = rootFolder,
            variant_count = targetPaths.Count,
            result_count = results.Count,
            results,
        };
    }

    static object ExecuteAction(JObject action)
    {
        string name = RequiredString(action, "action");
        return name switch
        {
            "open_prefab" => ActionOpenPrefab(action),
            "save_prefab" => ActionSavePrefab(),
            "close_prefab" => ActionClosePrefab(action),
            "create_game_object" => ActionCreateGameObject(action),
            "add_component" => ActionAddComponent(action),
            "set_rect_transform" => ActionSetRectTransform(action),
            "set_image" => ActionSetImage(action),
            "set_text_mesh_pro" => ActionSetTextMeshPro(action),
            "set_object_reference" => ActionSetObjectReference(action),
            _ => throw new Exception($"Unknown action: {name}"),
        };
    }

    static void EnsureBackupForAction(string actionName, JObject action, IDictionary<string, string> backups)
    {
        if (MutatingActions.Contains(actionName))
        {
            EnsureCurrentPrefabBackup(backups);
            return;
        }

        if (actionName.Equals("save_prefab", StringComparison.OrdinalIgnoreCase))
        {
            EnsureCurrentPrefabBackup(backups);
            return;
        }

        if (actionName.Equals("close_prefab", StringComparison.OrdinalIgnoreCase))
        {
            bool save = action["save"]?.Value<bool>() ?? false;
            if (save)
                EnsureCurrentPrefabBackup(backups);
        }
    }

    static object ActionOpenPrefab(JObject action)
    {
        string prefabPath = RequiredString(action, "prefab_path");
        OpenPrefab(prefabPath);
        return new
        {
            success = true,
            prefab_path = _openPrefabPath,
            root_name = _openPrefabRoot.name,
        };
    }

    static object ActionSavePrefab()
    {
        EnsurePrefabOpen();
        SaveCurrentPrefab();
        return new { success = true, prefab_path = _openPrefabPath };
    }

    static object ActionClosePrefab(JObject action)
    {
        bool save = action["save"]?.Value<bool>() ?? false;
        CloseCurrentPrefab(save);
        return new { success = true, closed = true, saved = save };
    }

    static object ActionCreateGameObject(JObject action)
    {
        EnsurePrefabOpen();
        string name = RequiredString(action, "name");
        string parentPath = action["parent_path"]?.ToString();
        bool addRectTransform = action["rect_transform"]?.Value<bool>() ?? false;
        bool asFirstSibling = action["as_first_sibling"]?.Value<bool>() ?? false;
        bool asLastSibling = action["as_last_sibling"]?.Value<bool>() ?? false;

        var parent = string.IsNullOrWhiteSpace(parentPath)
            ? _openPrefabRoot
            : FindGameObjectByPath(_openPrefabRoot, parentPath)
              ?? throw new Exception($"Could not find parent_path: {parentPath}");

        var go = new GameObject(name);
        go.transform.SetParent(parent.transform, false);
        if (addRectTransform && go.GetComponent<RectTransform>() == null)
            go.AddComponent<RectTransform>();
        if (asFirstSibling)
            go.transform.SetAsFirstSibling();
        if (asLastSibling)
            go.transform.SetAsLastSibling();

        MarkDirty(go);
        return new
        {
            success = true,
            path = GetGameObjectPath(go, _openPrefabRoot),
        };
    }

    static object ActionAddComponent(JObject action)
    {
        EnsurePrefabOpen();
        string targetPath = RequiredString(action, "target_path");
        string componentTypeName = RequiredString(action, "component_type");
        bool allowDuplicate = action["allow_duplicate"]?.Value<bool>() ?? true;

        var go = FindGameObjectByPath(_openPrefabRoot, targetPath)
                 ?? throw new Exception($"Could not find target_path: {targetPath}");
        Type type = ResolveComponentType(componentTypeName);
        Component component = allowDuplicate ? go.AddComponent(type) : go.GetComponent(type) ?? go.AddComponent(type);

        MarkDirty(component);
        return new
        {
            success = true,
            target_path = targetPath,
            component_type = component.GetType().FullName,
        };
    }

    static object ActionSetRectTransform(JObject action)
    {
        EnsurePrefabOpen();
        string targetPath = RequiredString(action, "target_path");
        var go = FindGameObjectByPath(_openPrefabRoot, targetPath)
                 ?? throw new Exception($"Could not find target_path: {targetPath}");

        var rt = go.GetComponent<RectTransform>() ?? go.AddComponent<RectTransform>();
        if (TryReadVector2(action["anchor_min"], out var anchorMin)) rt.anchorMin = anchorMin;
        if (TryReadVector2(action["anchor_max"], out var anchorMax)) rt.anchorMax = anchorMax;
        if (TryReadVector2(action["pivot"], out var pivot)) rt.pivot = pivot;
        if (TryReadVector2(action["anchored_position"], out var anchoredPosition)) rt.anchoredPosition = anchoredPosition;
        if (TryReadVector2(action["size_delta"], out var sizeDelta)) rt.sizeDelta = sizeDelta;
        if (TryReadVector2(action["offset_min"], out var offsetMin)) rt.offsetMin = offsetMin;
        if (TryReadVector2(action["offset_max"], out var offsetMax)) rt.offsetMax = offsetMax;

        MarkDirty(rt);
        return new
        {
            success = true,
            target_path = targetPath,
        };
    }

    static object ActionSetImage(JObject action)
    {
        EnsurePrefabOpen();
        string targetPath = RequiredString(action, "target_path");
        var go = FindGameObjectByPath(_openPrefabRoot, targetPath)
                 ?? throw new Exception($"Could not find target_path: {targetPath}");

        var image = go.GetComponent<Image>() ?? go.AddComponent<Image>();
        if (TryReadColor(action["color"], out var color))
            image.color = color;

        if (action.TryGetValue("raycast_target", out var raycastToken))
            image.raycastTarget = raycastToken.Value<bool>();

        string spritePath = action["sprite_path"]?.ToString();
        if (!string.IsNullOrWhiteSpace(spritePath))
        {
            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(spritePath);
            if (sprite == null)
                throw new Exception($"Sprite not found at: {spritePath}");
            image.sprite = sprite;
        }

        MarkDirty(image);
        return new
        {
            success = true,
            target_path = targetPath,
        };
    }

    static object ActionSetTextMeshPro(JObject action)
    {
        EnsurePrefabOpen();
        string targetPath = RequiredString(action, "target_path");
        var go = FindGameObjectByPath(_openPrefabRoot, targetPath)
                 ?? throw new Exception($"Could not find target_path: {targetPath}");

        var tmp = go.GetComponent<TextMeshProUGUI>() ?? go.AddComponent<TextMeshProUGUI>();
        if (action.TryGetValue("text", out var textToken)) tmp.text = textToken.ToString();
        if (action.TryGetValue("font_size", out var sizeToken)) tmp.fontSize = sizeToken.Value<float>();
        if (action.TryGetValue("alignment", out var alignToken))
            tmp.alignment = ParseEnum<TextAlignmentOptions>(alignToken.ToString(), "alignment");
        if (action.TryGetValue("font_style", out var styleToken))
            tmp.fontStyle = ParseEnum<FontStyles>(styleToken.ToString(), "font_style");
        if (action.TryGetValue("raycast_target", out var raycastToken))
            tmp.raycastTarget = raycastToken.Value<bool>();
        if (TryReadColor(action["color"], out var color))
            tmp.color = color;

        MarkDirty(tmp);
        return new
        {
            success = true,
            target_path = targetPath,
        };
    }

    static object ActionSetObjectReference(JObject action)
    {
        EnsurePrefabOpen();

        string targetPath = RequiredString(action, "target_path");
        string propertyPath = RequiredString(action, "property_path");
        string componentTypeName = action["component_type"]?.ToString();
        int? componentIndex = action["component_index"]?.Value<int?>();

        var go = FindGameObjectByPath(_openPrefabRoot, targetPath)
                 ?? throw new Exception($"Could not find target_path: {targetPath}");
        var component = ResolveComponentForProperty(go, propertyPath, componentTypeName, componentIndex);

        var serialized = new SerializedObject(component);
        var property = serialized.FindProperty(propertyPath);
        if (property == null)
            throw new Exception($"Property not found: {propertyPath}");
        if (property.propertyType != SerializedPropertyType.ObjectReference)
        {
            throw new Exception(
                $"Property '{propertyPath}' is {property.propertyType}, expected ObjectReference");
        }

        UnityEngine.Object reference = ResolveReferenceValue(action);
        property.objectReferenceValue = reference;
        serialized.ApplyModifiedPropertiesWithoutUndo();

        MarkDirty(component);
        return new
        {
            success = true,
            target_path = targetPath,
            component_type = component.GetType().FullName,
            property_path = propertyPath,
            value = DescribeObjectReference(reference, _openPrefabRoot),
        };
    }

    static (string ComponentType, bool IsNull, JObject Reference) ReadObjectReference(
        string prefabPath,
        string targetPath,
        string propertyPath,
        string componentTypeName,
        int? componentIndex)
    {
        return WithPrefabRoot(prefabPath, root =>
        {
            var go = FindGameObjectByPath(root, targetPath)
                     ?? throw new Exception($"Could not find target_path: {targetPath}");

            var component = ResolveComponentForProperty(go, propertyPath, componentTypeName, componentIndex);
            var serialized = new SerializedObject(component);
            var property = serialized.FindProperty(propertyPath);
            if (property == null)
                throw new Exception($"Property not found: {propertyPath}");
            if (property.propertyType != SerializedPropertyType.ObjectReference)
            {
                throw new Exception(
                    $"Property '{propertyPath}' is {property.propertyType}, expected ObjectReference");
            }

            var reference = DescribeObjectReference(property.objectReferenceValue, root);
            bool isNull = reference["is_null"]?.Value<bool>() ?? true;
            return (component.GetType().FullName, isNull, reference);
        });
    }

    static void CollectNode(Transform node, Transform prefabRoot, JArray nodes, bool includeValues, int maxProps)
    {
        var components = new JArray();
        foreach (var component in node.GetComponents<Component>())
        {
            if (component == null)
            {
                components.Add(new JObject
                {
                    ["type"] = "MissingComponent",
                });
                continue;
            }

            components.Add(new JObject
            {
                ["type"] = component.GetType().FullName,
                ["serialized_fields"] = CollectSerializedFields(component, prefabRoot, includeValues, maxProps),
            });
        }

        nodes.Add(new JObject
        {
            ["path"] = GetGameObjectPath(node.gameObject, prefabRoot.gameObject),
            ["name"] = node.name,
            ["component_count"] = components.Count,
            ["components"] = components,
        });

        foreach (Transform child in node)
            CollectNode(child, prefabRoot, nodes, includeValues, maxProps);
    }

    static JArray CollectSerializedFields(Component component, Transform prefabRoot, bool includeValues, int maxProps)
    {
        var fields = new JArray();
        var serialized = new SerializedObject(component);
        var iterator = serialized.GetIterator();
        bool enterChildren = true;
        int count = 0;

        while (iterator.NextVisible(enterChildren))
        {
            enterChildren = false;
            if (iterator.propertyPath == "m_Script")
                continue;

            var field = new JObject
            {
                ["path"] = iterator.propertyPath,
                ["type"] = iterator.propertyType.ToString(),
            };

            if (includeValues)
            {
                object value = ReadSerializedValue(iterator);
                field["value"] = value == null ? JValue.CreateNull() : JToken.FromObject(value);
                if (iterator.propertyType == SerializedPropertyType.ObjectReference)
                    field["reference"] = DescribeObjectReference(iterator.objectReferenceValue, prefabRoot.gameObject);
            }

            fields.Add(field);
            count++;
            if (count >= maxProps)
            {
                fields.Add(new JObject
                {
                    ["path"] = "__truncated__",
                    ["type"] = "Notice",
                    ["value"] = $"Stopped after {maxProps} serialized fields.",
                });
                break;
            }
        }

        return fields;
    }

    static object ReadSerializedValue(SerializedProperty property)
    {
        return property.propertyType switch
        {
            SerializedPropertyType.Integer => property.intValue,
            SerializedPropertyType.Boolean => property.boolValue,
            SerializedPropertyType.Float => property.floatValue,
            SerializedPropertyType.String => property.stringValue,
            SerializedPropertyType.Enum => property.enumDisplayNames != null &&
                                           property.enumValueIndex >= 0 &&
                                           property.enumValueIndex < property.enumDisplayNames.Length
                ? property.enumDisplayNames[property.enumValueIndex]
                : property.enumValueIndex.ToString(),
            SerializedPropertyType.ObjectReference => property.objectReferenceValue?.name,
            SerializedPropertyType.Vector2 => property.vector2Value.ToString(),
            SerializedPropertyType.Vector3 => property.vector3Value.ToString(),
            SerializedPropertyType.Color => property.colorValue.ToString(),
            SerializedPropertyType.Rect => property.rectValue.ToString(),
            SerializedPropertyType.Bounds => property.boundsValue.ToString(),
            SerializedPropertyType.Quaternion => property.quaternionValue.eulerAngles.ToString(),
            _ => property.displayName,
        };
    }

    static JObject DescribeObjectReference(UnityEngine.Object reference, GameObject prefabRoot)
    {
        if (reference == null)
        {
            return new JObject
            {
                ["kind"] = "null",
                ["is_null"] = true,
            };
        }

        string assetPath = AssetDatabase.GetAssetPath(reference);
        if (!string.IsNullOrWhiteSpace(assetPath))
        {
            return new JObject
            {
                ["kind"] = "asset",
                ["is_null"] = false,
                ["asset_path"] = assetPath,
                ["object_name"] = reference.name,
                ["object_type"] = reference.GetType().FullName,
            };
        }

        if (reference is GameObject go)
        {
            return new JObject
            {
                ["kind"] = "prefab_object",
                ["is_null"] = false,
                ["target_path"] = GetGameObjectPath(go, prefabRoot),
                ["object_name"] = go.name,
                ["object_type"] = go.GetType().FullName,
            };
        }

        if (reference is Component component)
        {
            return new JObject
            {
                ["kind"] = "prefab_component",
                ["is_null"] = false,
                ["target_path"] = GetGameObjectPath(component.gameObject, prefabRoot),
                ["component_type"] = component.GetType().FullName,
                ["object_name"] = component.name,
                ["object_type"] = component.GetType().FullName,
            };
        }

        return new JObject
        {
            ["kind"] = "object",
            ["is_null"] = false,
            ["object_name"] = reference.name,
            ["object_type"] = reference.GetType().FullName,
        };
    }

    static UnityEngine.Object ResolveReferenceValue(JObject action)
    {
        bool clear = action["clear"]?.Value<bool>() ?? false;
        if (clear)
            return null;

        string referenceAssetPath = action["reference_asset_path"]?.ToString();
        if (!string.IsNullOrWhiteSpace(referenceAssetPath))
        {
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(referenceAssetPath);
            if (asset == null)
                throw new Exception($"Could not load reference_asset_path: {referenceAssetPath}");
            return asset;
        }

        string referenceTargetPath = action["reference_target_path"]?.ToString();
        if (!string.IsNullOrWhiteSpace(referenceTargetPath))
        {
            var targetGo = FindGameObjectByPath(_openPrefabRoot, referenceTargetPath)
                           ?? throw new Exception($"Could not find reference_target_path: {referenceTargetPath}");
            string referenceComponentType = action["reference_component_type"]?.ToString();
            int? referenceComponentIndex = action["reference_component_index"]?.Value<int?>();
            if (string.IsNullOrWhiteSpace(referenceComponentType) && !referenceComponentIndex.HasValue)
                return targetGo;

            return ResolveComponentForProperty(
                targetGo,
                propertyPath: null,
                componentTypeName: referenceComponentType,
                componentIndex: referenceComponentIndex);
        }

        throw new Exception(
            "Provide one of: clear=true, reference_asset_path, reference_target_path.");
    }

    static Component ResolveComponentForProperty(
        GameObject go,
        string propertyPath,
        string componentTypeName,
        int? componentIndex)
    {
        if (go == null)
            throw new Exception("Target GameObject is null.");

        if (!string.IsNullOrWhiteSpace(componentTypeName))
        {
            Type type = ResolveComponentType(componentTypeName);
            var components = go.GetComponents(type);
            int index = componentIndex ?? 0;
            if (index < 0 || index >= components.Length)
                throw new Exception(
                    $"component_index {index} is out of range for type {componentTypeName} on {go.name}");
            return components[index];
        }

        if (componentIndex.HasValue)
        {
            var components = go.GetComponents<Component>();
            int index = componentIndex.Value;
            if (index < 0 || index >= components.Length)
                throw new Exception($"component_index {index} is out of range on {go.name}");
            return components[index];
        }

        if (string.IsNullOrWhiteSpace(propertyPath))
            throw new Exception("component_type/component_index required when property_path is not provided.");

        foreach (var component in go.GetComponents<Component>())
        {
            if (component == null)
                continue;

            var serialized = new SerializedObject(component);
            if (serialized.FindProperty(propertyPath) != null)
                return component;
        }

        throw new Exception(
            $"No component on '{go.name}' contains serialized property '{propertyPath}'.");
    }

    static Type ResolveComponentType(string componentTypeName)
    {
        if (string.IsNullOrWhiteSpace(componentTypeName))
            throw new Exception("component_type required");

        Type type = Type.GetType(componentTypeName, false);
        if (type == null)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                type = assembly.GetType(componentTypeName, false);
                if (type != null)
                    break;
            }
        }

        if (type == null)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch
                {
                    continue;
                }

                type = types.FirstOrDefault(t =>
                    string.Equals(t.Name, componentTypeName, StringComparison.Ordinal));
                if (type != null)
                    break;
            }
        }

        if (type == null)
            throw new Exception($"Could not resolve component type: {componentTypeName}");
        if (!typeof(Component).IsAssignableFrom(type))
            throw new Exception($"Type is not a Unity Component: {componentTypeName}");
        return type;
    }

    static bool PrefabHasComponent(string prefabPath, Type type, bool includeChildren)
    {
        var root = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
        if (root == null)
            return false;
        return includeChildren
            ? root.GetComponentInChildren(type, true) != null
            : root.GetComponent(type) != null;
    }

    static bool DependsOnAsset(string prefabPath, string assetPath)
    {
        assetPath = NormalizeAssetPath(assetPath);
        var deps = AssetDatabase.GetDependencies(prefabPath, true);
        return deps.Any(dep => AssetPathsEqual(dep, assetPath));
    }

    static bool InheritsFromPrefab(string prefabPath, string basePrefabPath)
    {
        basePrefabPath = NormalizeAssetPath(basePrefabPath);
        prefabPath = NormalizeAssetPath(prefabPath);
        if (AssetPathsEqual(prefabPath, basePrefabPath))
            return true;

        var current = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
        if (current == null)
            return false;

        while (true)
        {
            var source = PrefabUtility.GetCorrespondingObjectFromSource(current) as GameObject;
            if (source == null)
                return false;

            string sourcePath = AssetDatabase.GetAssetPath(source);
            if (AssetPathsEqual(sourcePath, basePrefabPath))
                return true;

            current = source;
        }
    }

    static void EnsureCurrentPrefabBackup(IDictionary<string, string> backups)
    {
        EnsurePrefabOpen();
        if (backups.ContainsKey(_openPrefabPath))
            return;

        string sourceAbsolutePath = ToAbsolutePath(_openPrefabPath);
        if (!File.Exists(sourceAbsolutePath))
            throw new Exception($"Prefab file missing on disk: {_openPrefabPath}");

        string backupPath = Path.Combine(
            Path.GetTempPath(),
            $"unity-mcp-{Guid.NewGuid():N}.prefab.bak");
        File.Copy(sourceAbsolutePath, backupPath, true);
        backups[_openPrefabPath] = backupPath;
    }

    static void Rollback(IDictionary<string, string> backups)
    {
        CloseCurrentPrefab(save: false);

        var errors = new List<string>();
        foreach (var pair in backups)
        {
            try
            {
                string destinationPath = ToAbsolutePath(pair.Key);
                File.Copy(pair.Value, destinationPath, true);
                AssetDatabase.ImportAsset(pair.Key, ImportAssetOptions.ForceUpdate);
            }
            catch (Exception ex)
            {
                errors.Add($"{pair.Key}: {ex.Message}");
            }
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        CleanupBackups(backups);

        if (errors.Count > 0)
            throw new Exception("Rollback encountered errors: " + string.Join("; ", errors));
    }

    static void CleanupBackups(IDictionary<string, string> backups)
    {
        foreach (var backup in backups.Values)
        {
            try
            {
                if (File.Exists(backup))
                    File.Delete(backup);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
        backups.Clear();
    }

    static T WithPrefabRoot<T>(string prefabPath, Func<GameObject, T> run)
    {
        prefabPath = NormalizeAssetPath(prefabPath);
        if (_openPrefabRoot != null && AssetPathsEqual(_openPrefabPath, prefabPath))
            return run(_openPrefabRoot);

        var tempRoot = PrefabUtility.LoadPrefabContents(prefabPath);
        if (tempRoot == null)
            throw new Exception($"Could not load prefab at: {prefabPath}");
        try
        {
            return run(tempRoot);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(tempRoot);
        }
    }

    static void OpenPrefab(string prefabPath)
    {
        prefabPath = NormalizeAssetPath(prefabPath);
        if (_openPrefabRoot != null)
        {
            if (AssetPathsEqual(_openPrefabPath, prefabPath))
                return;
            throw new Exception(
                $"A prefab is already open: {_openPrefabPath}. Close it before opening another.");
        }

        var root = PrefabUtility.LoadPrefabContents(prefabPath);
        if (root == null)
            throw new Exception($"Could not load prefab at: {prefabPath}");

        _openPrefabRoot = root;
        _openPrefabPath = prefabPath;
        _openPrefabDirty = false;
    }

    static void SaveCurrentPrefab()
    {
        EnsurePrefabOpen();
        PrefabUtility.SaveAsPrefabAsset(_openPrefabRoot, _openPrefabPath);
        AssetDatabase.SaveAssets();
        _openPrefabDirty = false;
    }

    static void CloseCurrentPrefab(bool save)
    {
        if (_openPrefabRoot == null)
            return;

        if (save && _openPrefabDirty)
            SaveCurrentPrefab();

        PrefabUtility.UnloadPrefabContents(_openPrefabRoot);
        _openPrefabRoot = null;
        _openPrefabPath = null;
        _openPrefabDirty = false;
    }

    static void EnsurePrefabOpen()
    {
        if (_openPrefabRoot == null)
            throw new Exception("No prefab is open. Use open_prefab first.");
    }

    static void MarkDirty(UnityEngine.Object obj)
    {
        if (obj != null)
            EditorUtility.SetDirty(obj);
        if (_openPrefabRoot != null)
            EditorUtility.SetDirty(_openPrefabRoot);
        _openPrefabDirty = true;
    }

    static GameObject FindGameObjectByPath(GameObject root, string path)
    {
        if (root == null)
            return null;
        if (string.IsNullOrWhiteSpace(path) || path == "/" || path == ".")
            return root;

        var segments = NormalizeHierarchyPath(path).Split('/');
        var current = root.transform;
        int index = 0;

        if (segments.Length > 0 && string.Equals(segments[0], root.name, StringComparison.Ordinal))
            index = 1;

        for (; index < segments.Length; index++)
        {
            current = current.Find(segments[index]);
            if (current == null)
                return null;
        }

        return current.gameObject;
    }

    static string GetGameObjectPath(GameObject go, GameObject root)
    {
        if (go == null)
            return "";
        if (root == null)
            return go.name;

        var names = new Stack<string>();
        var cursor = go.transform;
        while (cursor != null)
        {
            names.Push(cursor.name);
            if (cursor == root.transform)
                return string.Join("/", names);
            cursor = cursor.parent;
        }

        return go.name;
    }

    static string RequiredString(JObject obj, string key)
    {
        string value = obj[key]?.ToString();
        if (string.IsNullOrWhiteSpace(value))
            throw new Exception($"{key} required");
        return value;
    }

    static T ParseEnum<T>(string value, string field) where T : struct
    {
        if (Enum.TryParse<T>(value, true, out var parsed))
            return parsed;
        throw new Exception($"Invalid {field}: {value}");
    }

    static bool TryReadVector2(JToken token, out Vector2 value)
    {
        value = default;
        if (token == null)
            return false;

        if (token is JArray arr && arr.Count >= 2)
        {
            value = new Vector2(arr[0].Value<float>(), arr[1].Value<float>());
            return true;
        }

        if (token is JObject obj &&
            obj.TryGetValue("x", out var xToken) &&
            obj.TryGetValue("y", out var yToken))
        {
            value = new Vector2(xToken.Value<float>(), yToken.Value<float>());
            return true;
        }

        return false;
    }

    static bool TryReadColor(JToken token, out Color value)
    {
        value = default;
        if (token == null)
            return false;

        if (token is JArray arr && arr.Count >= 3)
        {
            float r = arr[0].Value<float>();
            float g = arr[1].Value<float>();
            float b = arr[2].Value<float>();
            float a = arr.Count > 3 ? arr[3].Value<float>() : 1f;

            if (r > 1f || g > 1f || b > 1f || a > 1f)
            {
                r /= 255f;
                g /= 255f;
                b /= 255f;
                a /= 255f;
            }

            value = new Color(r, g, b, a);
            return true;
        }

        if (token.Type == JTokenType.String)
        {
            string raw = token.ToString().TrimStart('#');
            if (ColorUtility.TryParseHtmlString("#" + raw, out value))
                return true;
        }

        return false;
    }

    static string NormalizeHierarchyPath(string path)
    {
        return path.Replace('\\', '/').Trim('/');
    }

    static string NormalizeAssetPath(string path)
    {
        return string.IsNullOrWhiteSpace(path) ? path : path.Replace('\\', '/');
    }

    static bool AssetPathsEqual(string left, string right)
    {
        return string.Equals(
            NormalizeAssetPath(left),
            NormalizeAssetPath(right),
            StringComparison.OrdinalIgnoreCase);
    }

    static string ToAbsolutePath(string assetPath)
    {
        string projectRoot = Directory.GetCurrentDirectory();
        return Path.GetFullPath(Path.Combine(projectRoot, NormalizeAssetPath(assetPath)));
    }

    static JArray DescribePrefabFileChanges(IDictionary<string, string> backups)
    {
        var results = new JArray();
        foreach (var pair in backups)
        {
            string prefabPath = pair.Key;
            string beforePath = pair.Value;
            string afterPath = ToAbsolutePath(prefabPath);
            string beforeHash = ComputeFileSha256(beforePath);
            string afterHash = ComputeFileSha256(afterPath);
            results.Add(new JObject
            {
                ["prefab_path"] = prefabPath,
                ["changed"] = !string.Equals(beforeHash, afterHash, StringComparison.Ordinal),
                ["before_sha256"] = beforeHash,
                ["after_sha256"] = afterHash,
            });
        }

        return results;
    }

    static string ComputeFileSha256(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(stream);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }

    static JArray DescribePlannedChanges(JArray actions)
    {
        var planned = new JArray();
        for (int i = 0; i < actions.Count; i++)
        {
            if (actions[i] is not JObject action)
            {
                planned.Add(new JObject
                {
                    ["index"] = i,
                    ["action"] = "invalid",
                    ["detail"] = "action entry is not an object",
                });
                continue;
            }

            string name = action["action"]?.ToString() ?? "";
            var row = new JObject
            {
                ["index"] = i,
                ["action"] = name,
            };

            switch (name)
            {
                case "create_game_object":
                    row["target_path"] = BuildCreatedGameObjectPath(action);
                    row["changes"] = new JArray("create_game_object");
                    break;
                case "add_component":
                    row["target_path"] = action["target_path"]?.ToString();
                    row["changes"] = new JArray($"add_component:{action["component_type"]?.ToString()}");
                    break;
                case "set_rect_transform":
                    row["target_path"] = action["target_path"]?.ToString();
                    row["changes"] = PresentKeys(
                        action,
                        "anchor_min",
                        "anchor_max",
                        "pivot",
                        "anchored_position",
                        "size_delta",
                        "offset_min",
                        "offset_max",
                        "rotation",
                        "local_scale");
                    break;
                case "set_image":
                    row["target_path"] = action["target_path"]?.ToString();
                    row["changes"] = PresentKeys(action, "color", "raycast_target", "sprite_path");
                    break;
                case "set_text_mesh_pro":
                    row["target_path"] = action["target_path"]?.ToString();
                    row["changes"] = PresentKeys(
                        action,
                        "text",
                        "font_size",
                        "alignment",
                        "font_style",
                        "color",
                        "raycast_target");
                    break;
                case "set_object_reference":
                    row["target_path"] = action["target_path"]?.ToString();
                    row["property_path"] = action["property_path"]?.ToString();
                    row["changes"] = PresentKeys(
                        action,
                        "clear",
                        "reference_asset_path",
                        "reference_target_path",
                        "reference_component_type",
                        "reference_component_index");
                    break;
                case "open_prefab":
                    row["target_path"] = action["prefab_path"]?.ToString();
                    row["changes"] = new JArray("open_prefab");
                    break;
                case "save_prefab":
                case "close_prefab":
                    row["changes"] = new JArray(name);
                    break;
                default:
                    row["changes"] = new JArray("unknown_action");
                    break;
            }

            planned.Add(row);
        }

        return planned;
    }

    static string BuildCreatedGameObjectPath(JObject action)
    {
        string parentPath = action["parent_path"]?.ToString();
        string name = action["name"]?.ToString();
        if (string.IsNullOrWhiteSpace(name))
            return parentPath;
        if (string.IsNullOrWhiteSpace(parentPath))
            return name;
        return NormalizeHierarchyPath(parentPath) + "/" + name;
    }

    static JArray PresentKeys(JObject action, params string[] keys)
    {
        var present = new JArray();
        foreach (string key in keys)
        {
            if (action.TryGetValue(key, out _))
                present.Add(key);
        }
        return present;
    }

    static JArray BuildVariantBatchActions(string prefabPath, JArray actions)
    {
        var batch = new JArray
        {
            new JObject
            {
                ["action"] = "open_prefab",
                ["prefab_path"] = prefabPath,
            },
        };

        for (int i = 0; i < actions.Count; i++)
        {
            if (actions[i] is not JObject action)
                throw new Exception($"actions[{i}] must be an object");

            string actionName = RequiredString(action, "action");
            if (LifecycleActions.Contains(actionName))
            {
                throw new Exception(
                    $"actions[{i}] cannot be '{actionName}'. Lifecycle is handled automatically.");
            }

            batch.Add(ReplaceTemplateTokens((JObject)action.DeepClone(), prefabPath));
        }

        batch.Add(new JObject { ["action"] = "save_prefab" });
        batch.Add(new JObject { ["action"] = "close_prefab", ["save"] = false });
        return batch;
    }

    static JObject ReplaceTemplateTokens(JObject action, string prefabPath)
    {
        foreach (var property in action.Properties().ToList())
            property.Value = ReplaceTemplateTokens(property.Value, prefabPath);
        return action;
    }

    static JToken ReplaceTemplateTokens(JToken token, string prefabPath)
    {
        switch (token.Type)
        {
            case JTokenType.Object:
                return ReplaceTemplateTokens((JObject)token, prefabPath);
            case JTokenType.Array:
                var array = new JArray();
                foreach (var child in token.Children())
                    array.Add(ReplaceTemplateTokens(child, prefabPath));
                return array;
            case JTokenType.String:
                string value = token.ToString();
                return value.IndexOf("{{prefab_path}}", StringComparison.Ordinal) >= 0
                    ? value.Replace("{{prefab_path}}", prefabPath)
                    : value;
            default:
                return token.DeepClone();
        }
    }
}
}
