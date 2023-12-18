﻿using ImGuiNET;
using Microsoft.Xna.Framework.Input;
using Murder.Core.Graphics;
using Murder.Diagnostics;
using Murder.Editor.ImGuiExtended;
using Murder.Editor.Stages;
using Murder.Prefabs;
using System.Collections.Immutable;
using System.Numerics;

namespace Murder.Editor.CustomEditors
{
    internal partial class WorldAssetEditor
    {
        private string _searchInstanceText = string.Empty;

        /// <summary>
        /// Last count of entities per group as of the last frame.
        /// </summary>
        private readonly Dictionary<string, int> _entitiesPerGroup = new();

        private void DrawEntitiesEditor(Stage stage)
        {
            ImGui.SeparatorText("Rooms");

            GameLogger.Verify(_asset is not null && Stages.ContainsKey(_asset.Guid));

            const string popupName = "New group";
            if (ImGuiHelpers.IconButton('\uf65e', $"add_group"))
            {
                _groupName = string.Empty;
                ImGui.OpenPopup(popupName);
            }

            ImGuiHelpers.HelpTooltip("Create a new folder");
            
            DrawCreateOrRenameGroupPopup(popupName);

            ImGui.SameLine();
            if (CanAddInstance && ImGuiHelpers.IconButton('\uf234', $"add_entity_world"))
            {
                ImGui.OpenPopup("Add entity");
            }

            ImGuiHelpers.HelpTooltip("Choose an entity to add");

            ImGui.PushItemWidth(-1);
            ImGui.SameLine();
            ImGui.InputTextWithHint("##search_assets", "Search...", ref _searchInstanceText, 256);
            ImGui.PopItemWidth();
            DrawAddEntityPopup("Add entity");

            ImGui.BeginChild("room list");
            {
                _selecting = -1;

                DrawEntityGroups(stage);

                if (TreeEntityGroupNode("Other Entities", Game.Profile.Theme.White, icon: '\uf500', flags: ImGuiTreeNodeFlags.DefaultOpen))
                {
                    DrawEntityList(group: null, Instances);

                    ImGui.TreePop();
                }
            }
            ImGui.EndChild();
        }

        private void DrawAddEntityPopup(string id, string? group = null)
        {
            if (ImGui.BeginPopup(id))
            {
                ImGui.BeginChild("add_child_world", size: new(x: ImGui.GetFontSize() * 20, ImGui.GetFontSize() * 3));

                ImGui.Text("\uf57e Choose a new entity to add");

                if (SearchBox.SearchInstantiableEntities() is Guid asset)
                {
                    EntityInstance instance = EntityBuilder.CreateInstance(asset);
                    AddInstance(instance);

                    if (group is not null)
                    {
                        MoveToGroup(group, instance.Guid);
                    }

                    ImGui.CloseCurrentPopup();
                }

                ImGui.EndChild();
                ImGui.EndPopup();
            }

        }

        /// <summary>
        /// Draw all folders with entities. Returns a list with all the entities grouped within those folders.
        /// </summary>
        protected virtual void DrawEntityGroups(Stage stage)
        {
            ImmutableDictionary<string, ImmutableArray<Guid>> folders = _world?.FetchFolders() ??
                ImmutableDictionary<string, ImmutableArray<Guid>>.Empty;


            string? hoveredGroup = stage.EditorHook.HoveringGroup;

            foreach ((string name, ImmutableArray<Guid> entities) in folders)
            {
                bool hovered = name.Equals(hoveredGroup, StringComparison.InvariantCultureIgnoreCase);
                ImGui.BeginGroup();

                ImGui.PushStyleColor(ImGuiCol.HeaderHovered, Game.Profile.Theme.Foreground);
                if (ImGui.TreeNodeEx($"###{name} ({entities.Length})", ImGuiTreeNodeFlags.AllowOverlap | ImGuiTreeNodeFlags.Framed | ImGuiTreeNodeFlags.Bullet))
                {
                    DrawGroupToggles($"{name} ({entities.Length})", name);

                    if (ImGuiHelpers.DeleteButton($"Delete_group_{name}"))
                    {
                        DeleteGroupWithEntities(name);
                    }

                    ImGui.SameLine();

                    string addEntityPopup = $"Add entity_{name}";
                    if (CanAddInstance && ImGuiHelpers.IconButton('\uf234', $"add_entity_world_{name}"))
                    {
                        ImGui.OpenPopup(addEntityPopup);
                    }

                    ImGuiHelpers.HelpTooltip("Choose an entity to add");
                    ImGui.SameLine();

                    string popupName = $"Rename group##{name}";
                    if (ImGuiHelpers.IconButton('\uf304', $"rename_group_{name}"))
                    {
                        _groupName = name;

                        ImGui.OpenPopup(popupName);
                    }

                    DrawAddEntityPopup(addEntityPopup, name);
                    DrawCreateOrRenameGroupPopup(popupName, previousName: name);

                    DrawEntityList(name, entities);
                    ImGui.TreePop();
                }
                else
                {
                    DrawGroupToggles($"{name} ({entities.Length})", name);
                }

                ImGui.EndGroup();

                // Draw a border on selected room
                if (hovered)
                {
                    Vector2 min = ImGui.GetItemRectMin();
                    Vector2 max = new Vector2(ImGui.GetContentRegionAvail().X + min.X, ImGui.GetItemRectMax().Y);

                    var dl = ImGui.GetWindowDrawList();

                    dl.AddRect(min, max, Color.ToUint(Game.Profile.Theme.White), 5);
                }
            }
        }

        private void DrawGroupToggles(string label, string groupName)
        {
            // Pop color on tree hover
            ImGui.PopStyleColor();

            ImGui.SameLine();
            
            ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
            ImGui.PushStyleColor(ImGuiCol.Text, Game.Profile.Theme.Faded);
            ImGui.Button("\uf058");
            ImGui.PopStyleColor();
            ImGuiHelpers.HelpTooltip("Not implemented yet...");

            ImGui.SameLine(0, 0);

            bool isVisible = IsGroupVisible(groupName);
            if (ImGui.Button(isVisible ? $"\uf06e##{groupName}" : $"\uf070##{groupName}"))
            {
                SwitchGroupVisibility(groupName, show: !isVisible);
            }

            ImGuiHelpers.HelpTooltip(isVisible ? "Hide" : "Show");

            ImGui.SameLine(0, 0);

            ImGui.Button("");
            ImGuiHelpers.HelpTooltip("Lock");

            ImGui.PopStyleColor();

            ImGui.SameLine();
            ImGui.Text(label);
        }

        private bool TreeEntityGroupNode(string name, System.Numerics.Vector4 textColor, char icon = '\ue1b0', ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.None) =>
            ImGuiHelpers.TreeNodeWithIconAndColor(
                icon: icon,
                label: name,
                flags: ImGuiTreeNodeFlags.Framed | ImGuiTreeNodeFlags.FramePadding | flags,
                text: textColor,
                background: Game.Profile.Theme.BgFaded,
                active: Game.Profile.Theme.Bg);

        /// <summary>
        /// Draw the entity list of <paramref name="group"/>.
        /// </summary>
        /// <param name="group">Group unique name. None if it doesn't belong to any.</param>
        /// <param name="entities">Entities that belong to the group.</param>
        private void DrawEntityList(string? group, IList<Guid> entities)
        {
            GameLogger.Verify(_asset is not null);

            if (entities.Count == 0)
            {
                ImGui.TextColored(Game.Profile.Theme.Faded, "Drag an entity here!");

                if (DragDrop<Guid>.DragDropTarget($"drag_instance", out Guid draggedId))
                {
                    MoveToGroup(group, draggedId, position: -1);
                }
            }

            int totalEntities = 0;
            for (int i = 0; i < entities.Count; ++i)
            {
                Guid entity = entities[i];

                // If we are showing all entities (group is null), only show entities that does not belong to any group.
                if (group is null && _world is not null && _world.BelongsToAnyGroup(entity))
                {
                    continue;
                }

                // Check if we should show this entity.
                EntityInstance? instance = TryFindInstance(entity);
                string? name = instance?.Name;

                // Filter entities if "Search..." has been used.
                if (!string.IsNullOrEmpty(_searchInstanceText) && instance is not null && name is not null)
                {
                    bool matchName = name.Contains(_searchInstanceText, StringComparison.OrdinalIgnoreCase);
                    bool matchComponent = instance.Components.Any(
                        c => c.GetType().Name.Contains(_searchInstanceText, StringComparison.OrdinalIgnoreCase));

                    if (!matchName && !matchComponent)
                    {
                        continue;
                    }
                }

                if (ImGuiHelpers.DeleteButton($"Delete_{entity}"))
                {
                    DeleteInstance(parent: null, entity);

                    continue;
                }

                ImGui.SameLine();

                ImGui.PushID($"Entity_bar_{entity}");

                bool isSelected = Stages[_asset.Guid].IsSelected(entity);

                if (ImGui.Selectable(name ?? "<?>", isSelected))
                {
                    _selecting = Stages[_asset.Guid].SelectEntity(entity, select: true);
                    if (_selecting is -1)
                    {
                        // Unable to find the entity. This probably means that it has been deactivated.
                        _selectedAsset = entity;
                    }
                    else
                    {
                        _selectedAsset = null;
                    }
                }

                DragDrop<Guid>.DragDropSource("drag_instance", name ?? "instance", entity);

                ImGui.PopID();

                if (DragDrop<Guid>.DragDropTarget($"drag_instance", out Guid draggedId))
                {
                    _world?.MoveToGroup(group, draggedId, i);
                }

                totalEntities++;
            }

            if (group is not null)
            {
                _entitiesPerGroup[group] = totalEntities;
            }
        }

        private string _groupName = "";

        private void DrawCreateOrRenameGroupPopup(string popupName, string? previousName = null)
        {
            bool isRename = previousName is not null;

            if (ImGui.BeginPopup(popupName))
            {
                if (!isRename)
                {
                    ImGui.Text("What's the new group name?");
                }

                ImGui.InputText("##group_name", ref _groupName, 128, ImGuiInputTextFlags.AutoSelectAll);

                string buttonLabel = isRename ? "Rename" : "Create";

                if (!string.IsNullOrWhiteSpace(_groupName) && _world is not null &&
                    !_world.FetchFolders().ContainsKey(_groupName))
                {
                    if (ImGui.Button(buttonLabel) || Game.Input.Pressed(Keys.Enter))
                    {
                        if (previousName is not null)
                        {
                            RenameGroup(previousName, _groupName);
                        }
                        else
                        {
                            AddGroup(_groupName);
                        }

                        ImGui.CloseCurrentPopup();
                    }
                }
                else
                {
                    ImGuiHelpers.DisabledButton(buttonLabel);
                    ImGuiHelpers.HelpTooltip("Unable to add group with duplicate or empty names.");
                }

                ImGui.EndPopup();
            }
        }

        /// <summary>
        /// Add a group to the world. This returns the valid name that it was able to create for this group.
        /// </summary>
        private string? AddGroup(string name)
        {
            if (_world is null)
            {
                GameLogger.Warning("Unable to add group without a world!");
                return null;
            }

            if (_world.HasGroup(name))
            {
                name += $" {_world.GroupsCount()}";

                return AddGroup(name);
            }

            _world.AddGroup(name);
            return name;
        }

        private void MoveToGroup(string? targetGroup, Guid instance, int position = -1)
        {
            GameLogger.Verify(_asset is not null);

            _world?.MoveToGroup(targetGroup, instance, position);
            Stages[_asset.Guid].SetGroupToEntity(instance, targetGroup);
        }

        private bool RenameGroup(string oldName, string newName)
        {
            GameLogger.Verify(_world is not null);

            if (AddGroup(newName) is not string actualName)
            {
                GameLogger.Error("Unable to add the rename group?");
                return false;
            }

            foreach (Guid instance in _world.FetchEntitiesOfGroup(oldName))
            {
                MoveToGroup(actualName, instance, position: -1);
            }

            _world.DeleteGroup(oldName);

            return true;
        }

        private bool IsGroupVisible(string groupName)
        {
            GameLogger.Verify(_world is not null);

            WorldStageInfo info = _worldStageInfo[_world.Guid];
            return !info.HiddenGroups.Contains(groupName);
        }

        private void SwitchGroupVisibility(string groupName, bool show)
        {
            GameLogger.Verify(_world is not null);

            WorldStageInfo info = _worldStageInfo[_world.Guid];
            ImmutableArray<Guid> entities = _world.FetchEntitiesOfGroup(groupName);

            if (show)
            {
                foreach (Guid g in entities)
                {
                    EntityInstance? instance = _world.TryGetInstance(g);
                    if (instance is not null)
                    {
                        ShowInstanceInEditor(parent: null, instance);
                    }
                }

                info.HiddenGroups.Remove(groupName);
            }
            else
            {
                info.HiddenGroups.Add(groupName);

                foreach (Guid g in entities)
                {
                    HideInstanceInEditor(g);
                }
            }
        }
    }
}