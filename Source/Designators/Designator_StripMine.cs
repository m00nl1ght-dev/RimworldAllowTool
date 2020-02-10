﻿using System;
using System.Collections.Generic;
using System.Linq;
using AllowTool.Settings;
using HugsLib;
using HugsLib.Settings;
using HugsLib.Utils;
using RimWorld;
using UnityEngine;
using Verse;

namespace AllowTool {
	[StaticConstructorOnStartup]
	public class Designator_StripMine : Designator_UnlimitedDragger {
		private const float InvalidCellHighlightAlpha = .2f;

		private static readonly Material areaOutlineMaterial = 
			(Material)AllowToolController.Instance.Reflection.GenDrawLineMatMetaOverlay.GetValue(null);
		private readonly Action updateCallback;
		private readonly MapCellHighlighter highlighter;
		private Material designationValidMat;
		private Material designationInvalidMat;
		private IntVec3 lastSelectionStart;
		private CellRect currentSelection;
		private bool updateCallbackScheduled;
		private StripMineWorldSettings worldSettings;
		private Dialog_StripMineSettings settingsWindow;
		private StripMineGlobalSettings globalSettings;

		private static SettingHandle<StripMineGlobalSettings> GlobalSettingsHandle {
			get { return AllowToolController.Instance.Handles.StripMineSettings; }
		}

		protected override DesignationDef Designation {
			get { return DesignationDefOf.Mine; }
		}

		public Designator_StripMine() {
			UseDesignatorDef(AllowToolDefOf.StripMineDesignator);
			highlighter = new MapCellHighlighter(EnumerateHighlightCells);
			Dragger.SelectionStart += DraggerOnSelectionStart;
			Dragger.SelectionChanged += DraggerOnSelectionChanged;
			Dragger.SelectionComplete += DraggerOnSelectionComplete;
			updateCallback = OnUpdate;
		}

		protected override void OnDefAssigned() {
			Material GetMaterial(Texture2D tex, float alpha) {
				return MaterialPool.MatFrom(tex, ShaderDatabase.MetaOverlay, new Color(1f, 1f, 1f, alpha));
			}
			Def.GetDragHighlightTexture(tex => {
				designationValidMat = GetMaterial(tex, 1f);
				designationInvalidMat = GetMaterial(tex, InvalidCellHighlightAlpha);
			});
		}

		public override void Selected() {
			base.Selected();
			ScheduleUpdateCallback();
			RevertToSavedWorldSettings();
			RevertToSavedGlobalSettings();
		}

		public override void DesignateMultiCell(IEnumerable<IntVec3> cells) {
			// do nothing. See DraggerOnSelectionComplete
		}

		private void CommitCurrentSelection() {
			DesignateCells(EnumerateDesignationCells());
			ClearCurrentSelection();
		}

		private void ClearCurrentSelection() {
			currentSelection = CellRect.Empty;
			lastSelectionStart = IntVec3.Zero;
			highlighter.ClearCachedCells();
		}

		private void DesignateCells(IEnumerable<IntVec3> targetCells) {
			var currentCell = IntVec3.Invalid;
			try {
				var map = Find.CurrentMap;
				var mineDef = DesignationDefOf.Mine;
				var alreadyDesignatedCells = new HashSet<IntVec3>(map.designationManager
					.SpawnedDesignationsOfDef(mineDef)
					.Select(d => d.target.Cell)
				);
				foreach (var cell in targetCells) {
					currentCell = cell;
					if (alreadyDesignatedCells.Contains(cell)) continue;
					cell.ToggleDesignation(DesignationDefOf.Mine, true);
				}
			} catch (Exception e) {
				AllowToolController.Logger.Error($"Error while placing Mine designations (cell {currentCell}): {e}");
			}
		}

		private void OnUpdate() {
			updateCallbackScheduled = false;
			var map = Find.CurrentMap;
			if (map != null && Find.DesignatorManager.SelectedDesignator == this) {
				ScheduleUpdateCallback();
				DrawCellRectOutline(currentSelection);
				highlighter.DrawCellHighlights();
			} else {
				OnDesignatorDeselected();
			}
		}

		private void OnDesignatorDeselected() {
			ClearCurrentSelection();
			if (settingsWindow != null) {
				settingsWindow.CancelAndClose();
				settingsWindow = null;
			}
			if (!GlobalSettingsHandle.Value.Equals(globalSettings)) {
				GlobalSettingsHandle.Value = globalSettings;
				HugsLibController.SettingsManager.SaveChanges();
				AllowToolController.Logger.Warning("Saved settings");
			}
		}

		private void ScheduleUpdateCallback() {
			if (updateCallbackScheduled) return;
			updateCallbackScheduled = true;
			HugsLibController.Instance.DoLater.DoNextUpdate(updateCallback);
		}

		private IEnumerable<IntVec3> EnumerateGridCells() {
			bool CellIsOnGridLine(IntVec3 c) {
				return (c.x - lastSelectionStart.x) % (worldSettings.HorizontalSpacing + 1) == 0
						|| (c.z - lastSelectionStart.z) % (worldSettings.VerticalSpacing + 1) == 0;
			}

			return currentSelection.Cells.Where(CellIsOnGridLine);
		}

		private IEnumerable<IntVec3> EnumerateDesignationCells() {
			var map = Find.CurrentMap;
			return EnumerateGridCells().Where(c => CellIsMineable(map, c));
		}

		private IEnumerable<MapCellHighlighter.Request> EnumerateHighlightCells() {
			var map = Find.CurrentMap;
			return EnumerateGridCells().Select(c =>
				new MapCellHighlighter.Request(c, CellIsMineable(map, c) ? designationValidMat : designationInvalidMat)
			);
		}

		private bool CellIsMineable(Map map, IntVec3 c) {
			if (c.Fogged(map)) return true;
			var m = c.GetFirstMineable(map);
			return m != null && m.def.mineable;
		}

		private void DraggerOnSelectionStart(CellRect cellRect) {
			currentSelection = cellRect;
			lastSelectionStart = Dragger.SelectionStartCell;
			highlighter.ClearCachedCells();
		}

		private void DraggerOnSelectionChanged(CellRect cellRect) {
			currentSelection = cellRect;
			highlighter.ClearCachedCells();
		}

		private void DraggerOnSelectionComplete(CellRect cellRect) {
			var invertMode = HugsLibUtility.ShiftIsHeld;
			var windowisOpen = settingsWindow != null;
			if (windowisOpen) {
				if (invertMode) {
					CommitCurrentSelection();
				} else {
					// wait for Accept
				}
			} else {
				var openWindow = globalSettings.ShowWindow;
				if (invertMode) {
					openWindow = !openWindow;
				}
				if (openWindow) {
					ShowSettingsWindow();
				} else {
					CommitCurrentSelection();
				}
			}
		}

		private void ShowSettingsWindow() {
			if(settingsWindow != null) return;
			settingsWindow = new Dialog_StripMineSettings(worldSettings) {
				ShowWindowToggleValue = globalSettings.ShowWindow,
				WindowPosition = globalSettings.WindowPosition
			};
			settingsWindow.SettingsChanged += WindowOnSettingsChanged;
			settingsWindow.Closing += WindowOnClosing;
			Find.WindowStack.Add(settingsWindow);
		}

		private void WindowOnSettingsChanged(IStripMineSettings stripMineSettings) {
			// show the spacing changes
			highlighter.ClearCachedCells();
		}

		private void WindowOnClosing(bool accept) {
			if (accept) {
				AllowToolController.Instance.WorldSettings.StripMineSettings = worldSettings.Clone();
				CommitCurrentSelection();
				globalSettings.ShowWindow = settingsWindow.ShowWindowToggleValue;
			} else {
				RevertToSavedWorldSettings();
				ClearCurrentSelection();
			}
			globalSettings.WindowPosition = settingsWindow.WindowPosition;
			settingsWindow = null;
		}

		private void RevertToSavedWorldSettings() {
			worldSettings = AllowToolController.Instance.WorldSettings.StripMineSettings?.Clone()
							?? new StripMineWorldSettings();
		}

		private void RevertToSavedGlobalSettings() {
			globalSettings = GlobalSettingsHandle.Value.Clone();
		}

		private static void DrawCellRectOutline(CellRect rect) {
			if (rect.Area == 0) return;
			var altitude = AltitudeLayer.MoteLow.AltitudeFor();
			Vector3 bottomLeft = new Vector3(rect.minX, altitude, rect.minZ),
				bottomRight = new Vector3(rect.maxX + 1f, altitude, rect.minZ),
				topLeft = new Vector3(rect.minX, altitude, rect.maxZ + 1f),
				topRight = new Vector3(rect.maxX + 1f, altitude, rect.maxZ + 1f);
			GenDraw.DrawLineBetween(topLeft, topRight, areaOutlineMaterial);
			GenDraw.DrawLineBetween(bottomLeft, bottomRight, areaOutlineMaterial);
			GenDraw.DrawLineBetween(topLeft, bottomLeft, areaOutlineMaterial);
			GenDraw.DrawLineBetween(topRight, bottomRight, areaOutlineMaterial);
		}
	}
}