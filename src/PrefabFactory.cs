using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Assets.Scripts;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Items;
using Assets.Scripts.Sound;
using LaunchPadBooster.Utils;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SerialTerminal
{
    /// <summary>
    /// Builds the mod's prefabs at Prefab.LoadAll time by cloning vanilla ones
    /// (the community "mirrored devices" pattern - no Unity editor, no asset bundles).
    /// </summary>
    public static class PrefabFactory
    {
        public const string TerminalPrefabName = "StructureSerialTerminal";
        public const string KitPrefabName = "ItemKitSerialTerminal";
        public const string SourceKitName = "ItemKitComputer";

        // Tried in order. StructureComputer = "Computer (Modern)".
        private static readonly string[] SourcePrefabFallbacks =
        {
            "StructureComputer",
            "StructureConsoleLED5Large",
            "StructureConsoleLED1x2"
        };

        private static bool _created;
        private static GameObject _root;

        public static void CreateAll()
        {
            if (_created)
            {
                return;
            }
            if (WorldManager.Instance == null)
            {
                SerialTerminalPlugin.Log.LogError("WorldManager not available; cannot register prefabs");
                return;
            }
            List<Thing> sourcePrefabs = WorldManager.Instance.SourcePrefabs;
            int terminalHash = Animator.StringToHash(TerminalPrefabName);
            if (sourcePrefabs.Exists(p => p != null && p.PrefabHash == terminalHash))
            {
                _created = true;
                return;
            }

            Structure sourceStructure = FindSourceStructure();
            MultiConstructor sourceKit = PrefabUtils.FindPrefab<MultiConstructor>(SourceKitName);
            if (sourceStructure == null || sourceKit == null)
            {
                SerialTerminalPlugin.Log.LogError(
                    $"Source prefabs not found (structure={sourceStructure != null}, kit={sourceKit != null}); has the game renamed them?");
                return;
            }

            try
            {
                _root = new GameObject("~SerialTerminalMod");
                _root.SetActive(false);
                Object.DontDestroyOnLoad(_root);

                SerialTerminalDevice terminal = CreateTerminal(sourceStructure);
                MultiConstructor kit = CreateKit(sourceKit, terminal);

                // Deconstructing the terminal must hand back our kit, not Kit (Consoles).
                foreach (BuildState state in terminal.BuildStates)
                {
                    if (state?.Tool != null && state.Tool.ToolExit != null)
                    {
                        state.Tool.ToolExit = kit;
                    }
                }

                // AddPrefabs registers with the SDK (flags the mod as required for
                // multiplayer join validation) and appends to SourcePrefabs on the
                // NEXT Prefab.LoadAll - our prefix is inside the current one, so the
                // direct adds below cover it. Both sides dedupe.
                SerialTerminalPlugin.MOD.AddPrefabs(new[] { terminal.gameObject, kit.gameObject });
                if (!sourcePrefabs.Contains(terminal)) sourcePrefabs.Add(terminal);
                if (!sourcePrefabs.Contains(kit)) sourcePrefabs.Add(kit);
                _created = true;
                SerialTerminalPlugin.Log.LogInfo(
                    $"Registered {TerminalPrefabName} ({terminal.PrefabHash}) and {KitPrefabName} ({kit.PrefabHash})");

                LogInteractables(terminal);
            }
            catch (Exception e)
            {
                SerialTerminalPlugin.Log.LogError("Failed to create prefabs: " + e);
            }
        }

        private static Structure FindSourceStructure()
        {
            foreach (string name in SourcePrefabFallbacks)
            {
                Structure structure = PrefabUtils.FindPrefab<Structure>(name);
                if (structure != null)
                {
                    SerialTerminalPlugin.Log.LogInfo("Cloning prefab " + name);
                    return structure;
                }
            }
            return null;
        }

        private static SerialTerminalDevice CreateTerminal(Structure source)
        {
            GameObject go = Object.Instantiate(source.gameObject, _root.transform);
            go.name = TerminalPrefabName;

            Thing old = go.GetComponent<Thing>();
            SerialTerminalDevice device = go.AddComponent<SerialTerminalDevice>();
            CopyFields(old, device);
            RedirectReferences(go, old, device);

            device.PrefabName = TerminalPrefabName;
            device.PrefabHash = Animator.StringToHash(TerminalPrefabName);

            AdoptSubObjects(device);

            // Owns the in-world screen (render texture + quad); inert on servers.
            TerminalScreenBehaviour screen = go.AddComponent<TerminalScreenBehaviour>();
            CaptureScreenAnchor(old, screen);
            CopySmartRotation(old, device);
            EnsureDigitTransform(device, go);

            Object.DestroyImmediate(old);

            // The TTY-6 has no motherboard: keep the access door permanently shut.
            device.Interactables.RemoveAll(i => i.Action == InteractableType.Open);

            EnsureActivateInteractable(device, CreateScreenCollider(device, screen));
            CloneExternalBlueprint(device, go);
            return device;
        }

        /// <summary>
        /// The vanilla Computer shows its motherboard UI on a world-space canvas; its
        /// transform is the exact pose + size of the monitor face, which is where our
        /// render-texture quad goes. The canvas itself must never activate (nothing
        /// drives it once the Computer component is gone).
        /// </summary>
        private static void CaptureScreenAnchor(Thing old, TerminalScreenBehaviour screen)
        {
            if (!(old is Computer computer) || computer.ComputerScreen == null)
            {
                return;
            }
            GameObject screenGo = computer.ComputerScreen;
            screen.ScreenAnchor = screenGo.transform;
            RectTransform rect = screenGo.GetComponent<RectTransform>();
            if (rect != null)
            {
                Vector3 scale = rect.lossyScale;
                screen.ScreenWorldWidth = Mathf.Abs(rect.rect.width * scale.x);
                screen.ScreenWorldHeight = Mathf.Abs(rect.rect.height * scale.y);
            }
            screenGo.SetActive(false);
            SerialTerminalPlugin.Log.LogInfo(
                $"Screen anchor '{screenGo.name}' {screen.ScreenWorldWidth:F3}x{screen.ScreenWorldHeight:F3} m");
        }

        /// <summary>
        /// Computer and LogicUnitBase each declare their own ISmartRotation fields
        /// (ConnectionType, OpenEndsPermutation); the shared-chain field copy misses
        /// them, and placement rotation goes wrong without the source's values.
        /// </summary>
        private static void CopySmartRotation(Thing old, SerialTerminalDevice device)
        {
            if (old is Computer computer)
            {
                device.ConnectionType = computer.ConnectionType;
                if (computer.OpenEndsPermutation != null)
                {
                    device.OpenEndsPermutation = (int[])computer.OpenEndsPermutation.Clone();
                }
            }
            SerialTerminalPlugin.Log.LogInfo(
                $"SmartRotation: connection={device.ConnectionType} permutation=[{string.Join(",", device.OpenEndsPermutation)}]"
                + $" rotationAxis={device.RotationAxis} placement={device.PlacementType}");
        }


        /// <summary>
        /// LogicDisplay.SetDisplay positions its digit glyphs via DigitTransform and
        /// dereferences it unconditionally. The glyphs never draw (the device vetoes
        /// the digit renderer's pool in OnAddToPool), but clones of non-LogicDisplay
        /// sources (Computer) leave the field null, so give it a throwaway anchor.
        /// </summary>
        private static void EnsureDigitTransform(SerialTerminalDevice device, GameObject go)
        {
            if (device.DigitTransform != null)
            {
                return;
            }
            GameObject anchor = new GameObject("SerialTerminalDigitAnchor");
            anchor.transform.SetParent(go.transform, worldPositionStays: false);
            device.DigitTransform = anchor.transform;
        }

        private static MultiConstructor CreateKit(MultiConstructor source, Structure constructable)
        {
            GameObject go = Object.Instantiate(source.gameObject, _root.transform);
            go.name = KitPrefabName;
            MultiConstructor kit = go.GetComponent<MultiConstructor>();
            kit.PrefabName = KitPrefabName;
            kit.PrefabHash = Animator.StringToHash(KitPrefabName);
            kit.Constructables.Clear();
            kit.Constructables.Add(constructable);
            foreach (Interactable interactable in kit.Interactables)
            {
                interactable.Parent = kit;
            }
            CloneExternalBlueprint(kit, go);
            return kit;
        }

        /// <summary>
        /// Copies every non-static, non-readonly field declared on each base class the
        /// source and replacement share, preserving the prefab's serialized configuration
        /// (meshes, connections, build states, power settings...). Types outside the
        /// shared chain (e.g. Computer when replacing with a LogicDisplay subclass) are
        /// skipped - their fields don't exist on the replacement.
        /// </summary>
        private static void CopyFields(Component from, Component to)
        {
            for (Type type = from.GetType(); type != null && type != typeof(MonoBehaviour); type = type.BaseType)
            {
                if (!type.IsInstanceOfType(to))
                {
                    continue;
                }
                foreach (FieldInfo field in type.GetFields(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (field.IsStatic || field.IsInitOnly)
                    {
                        continue;
                    }
                    field.SetValue(to, field.GetValue(from));
                }
            }
        }

        /// <summary>
        /// Anything in the prefab hierarchy holding a reference to the old LogicDisplay
        /// component gets repointed at the replacement - not just sibling components, but
        /// also the plain [Serializable] sub-objects they own (Connection, GameAudioEvent,
        /// Interactable, Slot, ThingRenderer...), each of which carries a Parent back-
        /// reference. Those are missed by a components-only sweep, and once the old
        /// component is destroyed Unity serializes the stale reference as null on every
        /// instance built from the prefab - which the game then dereferences blind
        /// (ConnectionRef..ctor, GameAudioEvent.IsValid, ...).
        /// </summary>
        private static void RedirectReferences(GameObject go, Component old, Component replacement)
        {
            HashSet<object> visited = new HashSet<object>(ReferenceComparer.Instance);
            foreach (MonoBehaviour behaviour in go.GetComponentsInChildren<MonoBehaviour>(includeInactive: true))
            {
                if (behaviour == null || behaviour == old || behaviour == replacement)
                {
                    continue;
                }
                Redirect(behaviour, old, replacement, visited, 0);
            }
        }

        private const int MaxRedirectDepth = 8;

        private static void Redirect(object target, Component old, Component replacement, HashSet<object> visited, int depth)
        {
            if (depth > MaxRedirectDepth || !visited.Add(target))
            {
                return;
            }

            if (target is IList list)
            {
                Type listType = list.GetType();
                Type elementType = listType.IsGenericType
                    ? listType.GetGenericArguments()[0]
                    : typeof(object);
                for (int i = 0; i < list.Count; i++)
                {
                    object element = list[i];
                    if (ReferenceEquals(element, old))
                    {
                        if (elementType.IsInstanceOfType(replacement))
                        {
                            list[i] = replacement;
                        }
                    }
                    else if (ShouldRecurseInto(element))
                    {
                        Redirect(element, old, replacement, visited, depth + 1);
                    }
                }
                return;
            }

            for (Type type = target.GetType(); type != null && type != typeof(MonoBehaviour) && type != typeof(object); type = type.BaseType)
            {
                foreach (FieldInfo field in type.GetFields(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (field.IsStatic)
                    {
                        continue;
                    }
                    object value = field.GetValue(target);
                    if (ReferenceEquals(value, old))
                    {
                        if (!field.IsInitOnly && field.FieldType.IsInstanceOfType(replacement))
                        {
                            field.SetValue(target, replacement);
                        }
                    }
                    else if (ShouldRecurseInto(value))
                    {
                        Redirect(value, old, replacement, visited, depth + 1);
                    }
                }
            }
        }

        /// <summary>
        /// Walk lists and the prefab's own serializable data classes only: other Unity
        /// objects are reached through the hierarchy sweep (or are not ours to rewrite),
        /// and structs would only be updated on a boxed copy.
        /// </summary>
        private static bool ShouldRecurseInto(object value)
        {
            if (value == null || value is UnityEngine.Object || value is string)
            {
                return false;
            }
            Type type = value.GetType();
            if (type.IsValueType)
            {
                return false;
            }
            return value is IList || type.Assembly == typeof(Thing).Assembly;
        }

        /// <summary>
        /// Every sub-object that carries an owner back-reference points at the terminal,
        /// including any the vanilla prefab left unset.
        /// </summary>
        private static void AdoptSubObjects(SerialTerminalDevice device)
        {
            foreach (Connection connection in device.OpenEnds)
            {
                connection.Parent = device;
            }
            foreach (Slot slot in device.Slots)
            {
                slot.Parent = device;
            }
            foreach (ThingRenderer renderer in device.Renderers)
            {
                renderer.Parent = device;
            }
            foreach (GameAudioEvent audioEvent in device.AudioEvents)
            {
                audioEvent.Parent = device;
            }
            foreach (Interactable interactable in device.Interactables)
            {
                interactable.Parent = device;
                foreach (GameAudioEvent audioEvent in interactable.AssociatedAudioEvents)
                {
                    audioEvent.Parent = device;
                }
            }
        }

        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();

            bool IEqualityComparer<object>.Equals(object x, object y) => ReferenceEquals(x, y);

            public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
        }

        /// <summary>
        /// A thin trigger box exactly over the monitor face, so the Activate target is
        /// the visible screen instead of whatever collider the fallback heuristic finds
        /// (on the Computer prefab that was the root collider, which the door/slot/button
        /// triggers all occlude).
        /// </summary>
        private static Collider CreateScreenCollider(SerialTerminalDevice device, TerminalScreenBehaviour screen)
        {
            Transform anchor = screen.ScreenAnchor;
            if (anchor == null || screen.ScreenWorldWidth <= 0f || screen.ScreenWorldHeight <= 0f)
            {
                return null;
            }
            GameObject go = new GameObject("SerialTerminalScreenCollider");
            go.transform.SetParent(anchor.parent, worldPositionStays: false);
            go.transform.localPosition = anchor.localPosition;
            go.transform.localRotation = anchor.localRotation;
            // Same layer as the prefab's other interaction triggers.
            go.layer = anchor.gameObject.layer;
            foreach (Interactable interactable in device.Interactables)
            {
                if (interactable.Collider != null)
                {
                    go.layer = interactable.Collider.gameObject.layer;
                    break;
                }
            }
            BoxCollider box = go.AddComponent<BoxCollider>();
            box.size = new Vector3(screen.ScreenWorldWidth, screen.ScreenWorldHeight, 0.03f);
            box.isTrigger = true;
            return box;
        }

        /// <summary>
        /// The source prefab has no Activate interaction; add one so the player can
        /// click the terminal and type. Prefers the dedicated screen collider; falls
        /// back to the largest collider no other interactable uses.
        /// </summary>
        private static void EnsureActivateInteractable(SerialTerminalDevice device, Collider preferred)
        {
            if (device.Interactables.Exists(i => i.Action == InteractableType.Activate))
            {
                return;
            }
            Collider target = preferred != null ? preferred : FindLargestUnusedCollider(device);
            if (target == null)
            {
                SerialTerminalPlugin.Log.LogWarning("No collider found for the Activate interaction; typing will be unavailable");
                return;
            }
            device.Interactables.Add(new Interactable
            {
                Parent = device,
                Action = InteractableType.Activate,
                Collider = target
            });
        }

        private static Collider FindLargestUnusedCollider(SerialTerminalDevice device)
        {
            HashSet<Collider> used = new HashSet<Collider>();
            foreach (Interactable interactable in device.Interactables)
            {
                if (interactable.Collider != null)
                {
                    used.Add(interactable.Collider);
                }
            }
            Collider best = null;
            float bestArea = 0f;
            foreach (Collider collider in device.GetComponentsInChildren<Collider>(includeInactive: true))
            {
                if (used.Contains(collider))
                {
                    continue;
                }
                Vector3 size = collider.bounds.size;
                float area = size.x * size.y + size.y * size.z + size.x * size.z;
                if (best == null || area > bestArea)
                {
                    best = collider;
                    bestArea = area;
                }
            }
            return best;
        }

        private static void CloneExternalBlueprint(Thing thing, GameObject owner)
        {
            if (thing.Blueprint == null || thing.Blueprint.transform.IsChildOf(owner.transform))
            {
                return;
            }
            GameObject blueprint = Object.Instantiate(thing.Blueprint, _root.transform);
            blueprint.name = thing.PrefabName + "Blueprint";
            thing.Blueprint = blueprint;
        }

        private static void LogInteractables(Thing thing)
        {
            StringBuilder sb = new StringBuilder("Terminal interactables: ");
            foreach (Interactable interactable in thing.Interactables)
            {
                sb.Append(interactable.Action).Append(
                    interactable.Collider != null ? "(" + interactable.Collider.name + ") " : "(no collider) ");
            }
            SerialTerminalPlugin.Log.LogInfo(sb.ToString());
        }
    }
}
