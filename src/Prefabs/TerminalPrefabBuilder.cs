using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Sound;
using SerialTerminal.Devices;
using UnityEngine;

namespace SerialTerminal.Prefabs
{
    /// <summary>
    /// The terminal-specific prefab build steps: swap the source Thing for a
    /// SerialTerminalDevice, capture the monitor face for the in-world screen,
    /// and shape the interactables. PrefabFactory orchestrates; PrefabSurgery
    /// provides the generic clone machinery.
    /// </summary>
    internal static class TerminalPrefabBuilder
    {
        /// <summary>Clones the source Computer into the finished terminal prefab.</summary>
        /// <param name="source">The vanilla Computer to clone (validated by PrefabFactory).</param>
        /// <param name="parent">Inactive holder object the prefab lives under.</param>
        public static SerialTerminalDevice CreateTerminal(Computer source, Transform parent)
        {
            GameObject go = Object.Instantiate(source.gameObject, parent);
            go.name = PrefabFactory.TerminalPrefabName;

            Computer old = go.GetComponent<Computer>();
            SerialTerminalDevice device = go.AddComponent<SerialTerminalDevice>();
            PrefabSurgery.CopyFields(old, device);
            PrefabSurgery.RedirectReferences(go, old, device);

            device.PrefabName = PrefabFactory.TerminalPrefabName;
            device.PrefabHash = Animator.StringToHash(PrefabFactory.TerminalPrefabName);

            AdoptSubObjects(device);

            // Owns the in-world screen (render texture + quad); inert on servers.
            TerminalScreenBehaviour screen = go.AddComponent<TerminalScreenBehaviour>();
            CaptureScreenAnchor(old, screen);
            CopySmartRotation(old, device);
            AddDigitTransform(device, go);

            Object.DestroyImmediate(old);

            // The TTY-6 has no motherboard: keep the access door permanently shut.
            _ = device.Interactables.RemoveAll(i => i.Action == InteractableType.Open);

            AddActivateInteractable(device, CreateScreenCollider(device, screen));
            PrefabSurgery.CloneExternalBlueprint(device, go, parent);
            return device;
        }

        /// <summary>Clones the source kit and points it at the terminal structure.</summary>
        /// <param name="source">The vanilla kit to clone.</param>
        /// <param name="constructable">The structure the kit builds.</param>
        /// <param name="parent">Inactive holder object the prefab lives under.</param>
        public static MultiConstructor CreateKit(MultiConstructor source, Structure constructable, Transform parent)
        {
            GameObject go = Object.Instantiate(source.gameObject, parent);
            go.name = PrefabFactory.KitPrefabName;
            MultiConstructor kit = go.GetComponent<MultiConstructor>();
            kit.PrefabName = PrefabFactory.KitPrefabName;
            kit.PrefabHash = Animator.StringToHash(PrefabFactory.KitPrefabName);
            kit.Constructables.Clear();
            kit.Constructables.Add(constructable);
            AdoptSubObjects(kit);
            PrefabSurgery.CloneExternalBlueprint(kit, go, parent);
            return kit;
        }

        /// <summary>
        /// The vanilla Computer shows its motherboard UI on a world-space canvas; its
        /// transform is the exact pose + size of the monitor face, which is where our
        /// render-texture quad goes. The canvas itself must never activate (nothing
        /// drives it once the Computer component is gone). PrefabFactory validated the
        /// canvas and its RectTransform on the source before cloning.
        /// </summary>
        /// <param name="computer">The clone's Computer component, before it is destroyed.</param>
        /// <param name="screen">The behaviour that stores the captured pose.</param>
        private static void CaptureScreenAnchor(Computer computer, TerminalScreenBehaviour screen)
        {
            GameObject screenGo = computer.ComputerScreen;
            RectTransform rect = screenGo.GetComponent<RectTransform>();
            Vector3 scale = rect.lossyScale;
            screen.ScreenAnchor = screenGo.transform;
            screen.ScreenWorldWidth = Mathf.Abs(rect.rect.width * scale.x);
            screen.ScreenWorldHeight = Mathf.Abs(rect.rect.height * scale.y);
            screenGo.SetActive(false);
        }

        /// <summary>
        /// Computer and LogicUnitBase each declare their own ISmartRotation fields
        /// (ConnectionType, OpenEndsPermutation); the shared-chain field copy misses
        /// them, and placement rotation goes wrong without the source's values.
        /// </summary>
        /// <param name="computer">The clone's Computer component.</param>
        /// <param name="device">The replacement device receiving the values.</param>
        private static void CopySmartRotation(Computer computer, SerialTerminalDevice device)
        {
            device.ConnectionType = computer.ConnectionType;
            if (computer.OpenEndsPermutation != null)
            {
                device.OpenEndsPermutation = (int[])computer.OpenEndsPermutation.Clone();
            }
        }

        /// <summary>
        /// LogicDisplay.SetDisplay positions its digit glyphs via DigitTransform and
        /// dereferences it unconditionally. The glyphs never draw (the device vetoes
        /// the digit renderer's pool in OnAddToPool), and the field is always null
        /// after the clone — Computer is not a LogicDisplay, so the shared-chain
        /// field copy never fills it — so give it a throwaway anchor.
        /// </summary>
        /// <param name="device">The device whose DigitTransform must be non-null.</param>
        /// <param name="go">The prefab root the anchor is parented to.</param>
        private static void AddDigitTransform(SerialTerminalDevice device, GameObject go)
        {
            GameObject anchor = new("SerialTerminalDigitAnchor");
            anchor.transform.SetParent(go.transform, worldPositionStays: false);
            device.DigitTransform = anchor.transform;
        }

        /// <summary>
        /// Every sub-object that carries an owner back-reference points at the given
        /// prefab component, including any the vanilla prefab left unset.
        /// </summary>
        /// <param name="owner">The prefab component every sub-object should point at.</param>
        private static void AdoptSubObjects(Thing owner)
        {
            // Connections exist on structures only (OpenEnds lives on SmallGrid).
            if (owner is SmallGrid grid)
            {
                foreach (Connection connection in grid.OpenEnds)
                {
                    connection.Parent = grid;
                }
            }
            foreach (Slot slot in owner.Slots)
            {
                slot.Parent = owner;
            }
            foreach (ThingRenderer renderer in owner.Renderers)
            {
                renderer.Parent = owner;
            }
            foreach (GameAudioEvent audioEvent in owner.AudioEvents)
            {
                audioEvent.Parent = owner;
            }
            foreach (Interactable interactable in owner.Interactables)
            {
                interactable.Parent = owner;
                foreach (GameAudioEvent audioEvent in interactable.AssociatedAudioEvents)
                {
                    audioEvent.Parent = owner;
                }
            }
        }

        /// <summary>
        /// A thin trigger box exactly over the monitor face, so the Activate target is
        /// the visible screen instead of the root collider (which the door/slot/button
        /// triggers all occlude on the Computer prefab).
        /// </summary>
        /// <param name="device">The terminal the collider is built for.</param>
        /// <param name="screen">Captured screen pose and size.</param>
        private static BoxCollider CreateScreenCollider(SerialTerminalDevice device, TerminalScreenBehaviour screen)
        {
            Transform anchor = screen.ScreenAnchor;
            GameObject go = new("SerialTerminalScreenCollider");
            go.transform.SetParent(anchor.parent, worldPositionStays: false);
            go.transform.SetLocalPositionAndRotation(anchor.localPosition, anchor.localRotation);
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
        /// The source prefab has no Activate interaction; add one on the dedicated
        /// screen collider so the player can click the terminal and type.
        /// </summary>
        /// <param name="device">The terminal to add the interaction to.</param>
        /// <param name="screenCollider">The dedicated screen collider.</param>
        private static void AddActivateInteractable(SerialTerminalDevice device, Collider screenCollider)
        {
            device.Interactables.Add(new Interactable
            {
                Parent = device,
                Action = InteractableType.Activate,
                Collider = screenCollider
            });
        }
    }
}
