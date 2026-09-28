#!/usr/bin/env python3
"""Check the M1 WPF shell's load-bearing accessibility and composition markers."""

from __future__ import annotations

import sys
import xml.etree.ElementTree as ET
from pathlib import Path


def main() -> int:
    root = Path(__file__).resolve().parents[2]
    xaml = (root / "src" / "NpcManager.Desktop" / "MainWindow.xaml").read_text(encoding="utf-8")
    app = (root / "src" / "NpcManager.Desktop" / "App.xaml.cs").read_text(encoding="utf-8")
    window = (root / "src" / "NpcManager.Desktop" / "MainWindow.xaml.cs").read_text(encoding="utf-8")
    progress = (root / "src" / "NpcManager.Desktop" / "RaceMenuNpcBuildProgressWindow.xaml").read_text(encoding="utf-8")
    progress_code = (root / "src" / "NpcManager.Desktop" / "RaceMenuNpcBuildProgressWindow.xaml.cs").read_text(encoding="utf-8")
    preset_npc_panel = (
        root
        / "src"
        / "NpcManager.Desktop"
        / "RaceMenuNpcBuildPanel.xaml"
    ).read_text(encoding="utf-8")
    reference_panel_path = (
        root
        / "src"
        / "NpcManager.Desktop"
        / "ReferencePresetAuthoringPanel.xaml"
    )
    main_workspace_panel_path = (
        root
        / "src"
        / "NpcManager.Desktop"
        / "SkyrimMainWorkspacePanel.xaml"
    )
    required_xaml = (
        'x:Class="NpcManager.Desktop.MainWindow"',
        "MinWidth=",
        "MinHeight=",
        "AutomationProperties.Name",
        'Command="{Binding ShowCapabilitiesCommand}"',
        'ItemsSource="{Binding Commands}"',
    )
    errors = [f"MainWindow.xaml missing marker: {marker}" for marker in required_xaml if marker not in xaml]
    if "System.Windows.Application" not in app:
        errors.append("App.xaml.cs does not derive from WPF Application.")
    if "InitializeComponent()" not in window or "DataContext" not in window:
        errors.append("MainWindow composition boundary is incomplete.")
    if not reference_panel_path.exists():
        errors.append("reference-preset-panel-missing")
    else:
        reference_panel = reference_panel_path.read_text(encoding="utf-8")
        required_reference_panel = (
            'x:Class="NpcManager.Desktop.ReferencePresetAuthoringPanel"',
            'AutomationProperties.Name="Reference preset authoring workspace"',
            "1 Define target",
            "2 Analyze",
            "3 Review",
            "4 Choose resources",
            "5 Compare",
            "6 Accept and write",
            'AutomationProperties.Name="Reference analysis progress"',
            'AutomationProperties.ItemStatus="{Binding ProgressText}"',
            'Property="automation:AutomationProperties.ItemStatus" Value="{Binding SourcePath.Value}"',
            'AutomationProperties.Name="Semantic anchor review grid"',
            'AutomationProperties.Name="Reference comparison region"',
            'AutomationProperties.ItemStatus="{Binding VerifiedPresetPath}"',
            'AutomationProperties.ItemStatus="{Binding VerifiedPresetHash}"',
            'Command="{Binding ApplyCommand}"',
            'Command="{Binding CancelCommand}"',
            "Continue to NPC",
        )
        errors.extend(
            f"ReferencePresetAuthoringPanel.xaml missing marker: {marker}"
            for marker in required_reference_panel
            if marker not in reference_panel
        )
        if reference_panel.count(
            'VirtualizingPanel.IsVirtualizing="False"'
        ) < 2:
            errors.append(
                "ReferencePresetAuthoringPanel.xaml must expose every review "
                "and binding anchor to UI Automation."
            )
    if not main_workspace_panel_path.exists():
        errors.append("main-workspace-panel-missing")
    else:
        main_workspace_panel = main_workspace_panel_path.read_text(
            encoding="utf-8"
        )
        required_main_workspace_panel = (
            'AutomationProperties.Name="Main workspace viewport"',
            'AutomationProperties.Name="NPC and leveled NPC record browser"',
            'VirtualizingPanel.IsVirtualizing="True"',
            'VirtualizingPanel.VirtualizationMode="Recycling"',
            'ScrollViewer.CanContentScroll="True"',
            'AutomationProperties.Name="Main workspace preview viewport"',
            'AutomationProperties.Name="Main workspace details viewport"',
        )
        errors.extend(
            f"SkyrimMainWorkspacePanel.xaml missing marker: {marker}"
            for marker in required_main_workspace_panel
            if marker not in main_workspace_panel
        )
        try:
            main_workspace_root = ET.fromstring(main_workspace_panel)
            main_workspace_children = list(main_workspace_root)
            if (
                len(main_workspace_children) != 1
                or main_workspace_children[0].tag
                != "{http://schemas.microsoft.com/winfx/2006/xaml/presentation}Grid"
            ):
                errors.append(
                    "SkyrimMainWorkspacePanel.xaml must expose one finite "
                    "root Grid; an outer ScrollViewer disables browser "
                    "virtualization."
                )
        except ET.ParseError as exception:
            errors.append(
                "SkyrimMainWorkspacePanel.xaml is not parseable XML: "
                f"{exception}"
            )
    reference_tab = xaml.find('Header="Create preset"')
    preset_to_npc_tab = xaml.find('Header="Preset to NPC"')
    if reference_tab < 0 or preset_to_npc_tab < 0:
        errors.append(
            "MainWindow.xaml does not expose both Create preset and Preset to NPC tasks."
        )
    elif reference_tab > preset_to_npc_tab:
        errors.append(
            "Create preset must appear immediately before Preset to NPC."
        )

    required_progress = (
        'x:Class="NpcManager.Desktop.RaceMenuNpcBuildProgressWindow"',
        'ResizeMode="NoResize"',
        'WindowStyle="None"',
        'ShowInTaskbar="False"',
        'AutomationProperties.Name="NPC build progress dialog"',
        'Value="{Binding ProgressPercent, Mode=OneWay}"',
        'Command="{Binding CancelCommand}"',
    )
    errors.extend(
        f"RaceMenuNpcBuildProgressWindow.xaml missing marker: {marker}"
        for marker in required_progress
        if marker not in progress
    )
    if "OnContentRendered" not in progress_code or "finally" not in progress_code or "Close();" not in progress_code:
        errors.append("Build-progress dialog does not own run-on-show automatic close.")

    required_preset_npc_status = (
        'AutomationProperties.Name="Preset NPC build status"',
        'AutomationProperties.ItemStatus="{Binding Status}"',
        'AutomationProperties.Name="Preset NPC build verdict"',
        'AutomationProperties.ItemStatus="{Binding Verdict}"',
        'AutomationProperties.Name="Verified preset NPC package manifest"',
        'AutomationProperties.ItemStatus="{Binding ResultPackage}"',
    )
    errors.extend(
        f"RaceMenuNpcBuildPanel.xaml missing marker: {marker}"
        for marker in required_preset_npc_status
        if marker not in preset_npc_panel
    )

    if errors:
        print("RESULT FAIL")
        for error in errors:
            print(f"  - {error}")
        return 1

    print("RESULT PASS WPF shell markers")
    return 0


if __name__ == "__main__":
    sys.exit(main())
