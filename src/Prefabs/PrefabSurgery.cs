using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Assets.Scripts.Objects;
using UnityEngine;

namespace SerialTerminal.Prefabs
{
    /// <summary>
    /// Generic clone-and-swap machinery for building prefabs out of vanilla
    /// ones: field copying across the shared base-class chain and repointing
    /// every reference at a replaced component. Knows nothing about the
    /// terminal; TerminalPrefabBuilder applies the device-specific steps.
    /// </summary>
    internal static class PrefabSurgery
    {
        /// <summary>
        /// Copies every non-static, non-readonly field declared on each base class the
        /// source and replacement share, preserving the prefab's serialized configuration
        /// (meshes, connections, build states, power settings...). Types outside the
        /// shared chain (e.g. Computer when replacing with a LogicDisplay subclass) are
        /// skipped - their fields don't exist on the replacement.
        /// </summary>
        /// <param name="from">The component to copy field values from.</param>
        /// <param name="to">The component to copy field values to.</param>
        public static void CopyFields(Component from, Component to)
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
        /// Anything in the prefab hierarchy holding a reference to the old component
        /// gets repointed at the replacement - not just sibling components, but
        /// also the plain [Serializable] sub-objects they own (Connection, GameAudioEvent,
        /// Interactable, Slot, ThingRenderer...), each of which carries a Parent back-
        /// reference. Those are missed by a components-only sweep, and once the old
        /// component is destroyed Unity serializes the stale reference as null on every
        /// instance built from the prefab - which the game then dereferences blind
        /// (ConnectionRef..ctor, GameAudioEvent.IsValid, ...).
        /// </summary>
        /// <param name="root">Root of the prefab hierarchy to sweep.</param>
        /// <param name="old">The component about to be destroyed.</param>
        /// <param name="replacement">The component references are repointed at.</param>
        public static void RedirectReferences(GameObject root, Component old, Component replacement)
        {
            Sweep sweep = new(old, replacement);
            foreach (MonoBehaviour behaviour in root.GetComponentsInChildren<MonoBehaviour>(includeInactive: true))
            {
                if (behaviour == null || behaviour == old || behaviour == replacement)
                {
                    continue;
                }
                sweep.Visit(behaviour, 0);
            }
        }

        /// <summary>
        /// The source prefab may share its construction Blueprint with the vanilla
        /// original; give the clone its own copy so later tweaks can't leak back.
        /// </summary>
        /// <param name="thing">The cloned prefab whose Blueprint may be external.</param>
        /// <param name="owner">The clone's root GameObject.</param>
        /// <param name="parent">Where the blueprint copy is parented.</param>
        public static void CloneExternalBlueprint(Thing thing, GameObject owner, Transform parent)
        {
            if (thing.Blueprint == null || thing.Blueprint.transform.IsChildOf(owner.transform))
            {
                return;
            }
            GameObject blueprint = UnityEngine.Object.Instantiate(thing.Blueprint, parent);
            blueprint.name = thing.PrefabName + "Blueprint";
            thing.Blueprint = blueprint;
        }

        /// <summary>
        /// One reference sweep: carries the old/replacement pair and the visited
        /// set so the recursive walk needs no parameter threading.
        /// </summary>
        /// <param name="old">The component about to be destroyed.</param>
        /// <param name="replacement">The component references are repointed at.</param>
        private sealed class Sweep(Component old, Component replacement)
        {
            private const int MaxDepth = 8;

            private readonly Component _old = old;
            private readonly Component _replacement = replacement;
            private readonly HashSet<object> _visited = new(ReferenceComparer.Instance);

            public void Visit(object target, int depth)
            {
                if (depth > MaxDepth || !_visited.Add(target))
                {
                    return;
                }

                if (target is IList list)
                {
                    VisitList(list, depth);
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
                        if (ReferenceEquals(value, _old))
                        {
                            if (!field.IsInitOnly && field.FieldType.IsInstanceOfType(_replacement))
                            {
                                field.SetValue(target, _replacement);
                            }
                        }
                        else if (ShouldRecurseInto(value))
                        {
                            Visit(value, depth + 1);
                        }
                    }
                }
            }

            private void VisitList(IList list, int depth)
            {
                Type listType = list.GetType();
                Type elementType = listType.IsGenericType
                    ? listType.GetGenericArguments()[0]
                    : typeof(object);
                for (int i = 0; i < list.Count; i++)
                {
                    object element = list[i];
                    if (ReferenceEquals(element, _old))
                    {
                        if (elementType.IsInstanceOfType(_replacement))
                        {
                            list[i] = _replacement;
                        }
                    }
                    else if (ShouldRecurseInto(element))
                    {
                        Visit(element, depth + 1);
                    }
                }
            }

            /// <summary>
            /// Walk lists and the prefab's own serializable data classes only: other Unity
            /// objects are reached through the hierarchy sweep (or are not ours to rewrite),
            /// and structs would only be updated on a boxed copy.
            /// </summary>
            /// <param name="value">The field or element value the sweep just read.</param>
            private static bool ShouldRecurseInto(object value)
            {
                if (value is null or UnityEngine.Object or string)
                {
                    return false;
                }
                Type type = value.GetType();
                return !type.IsValueType
                    && (value is IList || type.Assembly == typeof(Thing).Assembly);
            }
        }
    }
}
