﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DiabaseWizard
{
    class GCodeProcessor
    {
        private static readonly double DefaultFeedrate = 3000.0 / 60.0;         // see RRF in Configuration.h

        private static readonly double ToolChangeDuration = 4.0;                // in s
        private static readonly double ToolChangeDurationWithCleaning = 10.0;   // in s

        private FileStream input;
        private SettingsContainer settings;
        private Duet.MachineInfo machineInfo;
        private IProgress<int> progress;
        private IProgress<int> maxProgress;

        private List<GCodeLayer> layers;

        private class Coordinate
        {
            public double X;
            public double Y;
            public double Z;

            public Coordinate Clone() => new Coordinate { X = X, Y = Y, Z = Z };
            public void AssignFrom(Coordinate coord) { X = coord.X; Y = coord.Y; Z = coord.Z; }
        };
        private Coordinate lastPoint;

        public GCodeProcessor(FileStream stream, SettingsContainer preferences, Duet.MachineInfo machine,
            IProgress<int> setProgress, IProgress<int> setMaxProgress)
        {
            input = stream;
            settings = preferences;
            machineInfo = machine;
            progress = setProgress;
            maxProgress = setMaxProgress;

            layers = new List<GCodeLayer>();
            lastPoint = new Coordinate();
        }

        public async Task PreProcess()
        {
            StreamReader reader = new StreamReader(input);
            string lineBuffer = await reader.ReadLineAsync();
            if (lineBuffer == null)
            {
                throw new ProcessorException("File is empty");
            }

            if (lineBuffer.Contains("G-Code generated by Simplify3D(R)"))
            {
                maxProgress.Report((int)input.Length);

                double feedrate = DefaultFeedrate;
                int lineNumber = 1, selectedTool = -1;
                GCodeLayer layer = new GCodeLayer(0);

                do {
                    bool writeLine = true;
                    GCodeLine line = new GCodeLine(lineBuffer);

                    if (lineBuffer.StartsWith(";"))
                    {
                        if (lineBuffer.StartsWith("; layer "))
                        {
                            // Add past layer
                            layers.Add(layer);
                            layer = new GCodeLayer(layer.Number + 1);
                        }
                        else if ((layer.Number == 0 && lineNumber > 2 && !lineBuffer.Contains("layerHeight")) ||
                            lineBuffer.StartsWith("; tool") || lineBuffer.StartsWith("; process"))
                        {
                            // Keep first two comment lines but get rid of S3D process description and
                            // remove "; tool" as well as "; process" lines because they are completely useless
                            writeLine = false;
                        }
                    }
                    else
                    {
                        int? gCode = line.GetIValue('G');
                        if (gCode != null)
                        {
                            // G0 / G1
                            if (gCode == 0 || gCode == 1)
                            {
                                double? xParam = line.GetFValue('X');
                                double? yParam = line.GetFValue('Y');
                                double? zParam = line.GetFValue('Z');
                                double? eParam = line.GetFValue('E');
                                double? fParam = line.GetFValue('F');
                                if (xParam != null) { lastPoint.X = xParam.Value; }
                                if (yParam != null) { lastPoint.Y = yParam.Value; }
                                if (zParam != null) { lastPoint.Z = zParam.Value; }
                                if (eParam != null && selectedTool == -1) { writeLine = false; }
                                if (fParam != null) { feedrate = fParam.Value / 60.0; }
                            }
                            // G10
                            else if (gCode == 10)
                            {
                                int? pParam = line.GetIValue('P');
                                double? sParam = line.GetFValue('S');
                                if (pParam != null && pParam.Value > 0 && pParam.Value <= settings.Tools.Length &&
                                    sParam != null)
                                {
                                    // G10 P... S...
                                    settings.Tools[pParam.Value - 1].ActiveTemperature = (decimal)sParam.Value;
                                }
                            }
                        }
                        else
                        {
                            int? mCode = line.GetIValue('M');
                            if (mCode != null)
                            {
                                // M106
                                if (mCode == 106)
                                {
                                    // FIXME: Check machineInfo for non-thermostatic fans
                                    writeLine = false;
                                }
                                // M104
                                else if (mCode == 104)
                                {
                                    double? sParam = line.GetFValue('S');
                                    int? tParam = line.GetIValue('T');
                                    if (sParam != null &&
                                        tParam != null && tParam.Value > 0 && tParam.Value <= settings.Tools.Length)
                                    {
                                        ToolSettings toolSettings = settings.Tools[tParam.Value - 1];
                                        if (toolSettings.ActiveTemperature <= 0m)
                                        {
                                            toolSettings.ActiveTemperature = (decimal)sParam.Value;
                                            layer.AddLine($"G10 P{tParam} R{toolSettings.StandbyTemperature} S{toolSettings.ActiveTemperature}");
                                        }
                                        else
                                        {
                                            layer.AddLine($"G10 P{tParam} S{sParam}");
                                        }
                                        writeLine = false;
                                    }
                                }
                            }
                            else
                            {
                                // T-Code
                                int? tCode = line.GetIValue('T');
                                if (tCode != null)
                                {
                                    if (tCode > 0 && tCode <= settings.Tools.Length)
                                    {
                                        if (settings.Tools[tCode.Value - 1].Type == ToolType.Nozzle)
                                        {
                                            // Keep track of tools in use. Tool change sequences are inserted by the post-processor
                                            selectedTool = tCode.Value;
                                            writeLine = false;
                                        }
                                        else
                                        {
                                            // Make sure we don't print with inproperly configured tools...
                                            throw new ProcessorException($"Tool {tCode} is not configured as a nozzle (see line {lineNumber})");
                                        }
                                    }
                                    else
                                    {
                                        selectedTool = -1;
                                    }
                                }
                            }
                        }
                    }

                    // Add this line unless it was handled before
                    if (writeLine)
                    {
                        line.Tool = selectedTool;
                        line.Feedrate = feedrate;
                        layer.AddLine(line);
                    }
                    lineBuffer = await reader.ReadLineAsync();
                    lineNumber++;

                    // Report progress to the UI
                    progress.Report((int)input.Position);
                } while (lineBuffer != null);

                layers.Add(layer);
            }
            else if (lineBuffer.Contains("Diabase"))
            {
                throw new ProcessorException("File has been already processed");
            }
            else
            {
                throw new ProcessorException("File was not generated by Simplify3D");
            }
        }

        private void AddToolChange(GCodeLayer layer, int oldToolNumber, int newToolNumber)
        {
            if (oldToolNumber > 0 && oldToolNumber <= settings.Tools.Length)
            {
                ToolSettings oldTool = settings.Tools[oldToolNumber - 1];
                if (oldTool.PreheatTime > 0m)
                {
                    layer.AddLine($"G10 P{oldToolNumber} R{oldTool.StandbyTemperature}");
                }
            }

            ToolSettings newTool = settings.Tools[newToolNumber - 1];
            if (newTool.AutoClean)
            {
                if (oldToolNumber == -1 || newTool.PreheatTime <= 0m)
                {
                    layer.AddLine(new GCodeLine("T" + newToolNumber + " P0") { Tool = newToolNumber });
                    layer.AddLine("M116 P" + newToolNumber);
                }
                layer.AddLine(new GCodeLine("M98 P\"tprime" + newToolNumber + ".g\"") { Tool = newToolNumber });
            }
            else
            {
                layer.AddLine(new GCodeLine("T" + newToolNumber) { Tool = newToolNumber });
                if (oldToolNumber == -1 || newTool.PreheatTime <= 0m)
                {
                    layer.AddLine("M116 P" + newToolNumber);
                }
            }
        }

        public void PostProcess()
        {
            // Figure out how much we have to do
            int numIterations = 0;
            for(int i = 0; i < layers.Count; i++)
            {
                // This is for the island combination
                numIterations += layers[i].Lines.Count * settings.Tools.Length;

                // And this for the preheating
                if (i != 0)
                {
                    numIterations += layers[i].Lines.Count;
                }
            }
            maxProgress.Report(numIterations);

            // Combine tool islands per layer and adjust tool change sequences
            int iteration = 1, currentTool = -1;
            for(int layerIndex = 1; layerIndex < layers.Count; layerIndex++)
            {
                GCodeLayer layer = layers[layerIndex];
                GCodeLayer replacementLayer = new GCodeLayer(layer.Number);
                replacementLayer.Lines.Add(layer.Lines[0]);

                for(int toolNumber = 1; toolNumber <= settings.Tools.Length; toolNumber++)
                {
                    if (settings.Tools[toolNumber - 1].Type != ToolType.Nozzle)
                    {
                        // Don't bother with unconfigured tools
                        continue;
                    }

                    for(int lineIndex = 1; lineIndex < layer.Lines.Count; lineIndex++)
                    {
                        GCodeLine line = layer.Lines[lineIndex];
                        if (line.Tool == toolNumber)
                        {
                            replacementLayer.AddLine(line);

                            if (currentTool != toolNumber)
                            {
                                int? gCode = line.GetIValue('G');
                                if (gCode == 0 || gCode == 1)
                                {
                                    // Insert tool changes after first G0/G1 code
                                    AddToolChange(replacementLayer, currentTool, toolNumber);
                                    currentTool = toolNumber;
                                }
                            }
                        }
                    }
                }

                layers[layerIndex] = replacementLayer;
                progress.Report(iteration++);
            }

            // Add preheating sequences
            Coordinate position = lastPoint.Clone();
            Coordinate previousPosition = lastPoint.Clone();
            int selectedTool = -1;

            Dictionary<int, double> preheatCounters = new Dictionary<int, double>();   // Tool number vs. Elapsed time
            for (int layerIndex = layers.Count - 1; layerIndex >= 1; layerIndex--)
            {
                GCodeLayer layer = layers[layerIndex];
                for (int lineIndex = layer.Lines.Count - 1; lineIndex >= 0; lineIndex--)
                {
                    double timeSpent = 0;
                    GCodeLine line = layer.Lines[lineIndex];
                    if (selectedTool == -1)
                    {
                        selectedTool = line.Tool;
                    }
                    else if (selectedTool != line.Tool)
                    {
                        if (selectedTool >= 0 && selectedTool <= settings.Tools.Length)
                        {
                            // Take into account tool change times
                            ToolSettings tool = settings.Tools[selectedTool - 1];
                            timeSpent += (settings.Tools[selectedTool - 1].AutoClean) ? ToolChangeDurationWithCleaning : ToolChangeDuration;

                            // See if we need to use preheating for this tool
                            if (tool.PreheatTime > 0.0m)
                            {
                                if (preheatCounters.ContainsKey(selectedTool))
                                {
                                    preheatCounters[selectedTool] = 0.0;
                                }
                                else
                                {
                                    preheatCounters.Add(selectedTool, 0.0);
                                }
                            }
                        }
                        selectedTool = line.Tool;
                    }

                    // Any counters running?
                    if (preheatCounters.Count > 0)
                    {
                        int? gCode = line.GetIValue('G');

                        // G0 / G1
                        if (gCode == 0 || gCode == 1)
                        {
                            double? xParam = line.GetFValue('X');
                            double? yParam = line.GetFValue('Y');
                            double? zParam = line.GetFValue('Z');
                            if (xParam != null) { previousPosition.X = xParam.Value; }
                            if (yParam != null) { previousPosition.Y = yParam.Value; }
                            if (zParam != null) { previousPosition.Z = zParam.Value; }

                            double distance = Math.Sqrt(Math.Pow(position.X - previousPosition.X, 2) +
                                                        Math.Pow(position.Y - previousPosition.Y, 2) +
                                                        Math.Pow(position.Z - previousPosition.Z, 2));
                            double feedrate = line.Feedrate;
                            if (line.Feedrate > 0.0)
                            {
                                // TODO: Take into account E axis and accelerations here
                                timeSpent += distance / line.Feedrate;
                            }

                            position.AssignFrom(previousPosition);
                        }
                        // G4
                        else if (gCode == 4)
                        {
                            double? sParam = line.GetFValue('S');
                            if (sParam != null)
                            {
                                timeSpent += sParam.Value;
                            }
                            else
                            {
                                int? pParam = line.GetIValue('P');
                                if (pParam != null)
                                {
                                    timeSpent += pParam.Value * 1000.0;
                                }
                            }
                        }
                        // G10 P... R...
                        else if (gCode == 10)
                        {
                            int? pParam = line.GetIValue('P');
                            int? rParam = line.GetIValue('R');
                            if (pParam != null && rParam != null && pParam > 0 && pParam <= settings.Tools.Length)
                            {
                                if (preheatCounters.ContainsKey(pParam.Value))
                                {
                                    // Remove this line again if we are still preheating
                                    layer.Lines.RemoveAt(lineIndex);
                                }
                            }
                        }

                        foreach(int toolNumber in preheatCounters.Keys.ToList())
                        {
                            ToolSettings tool = settings.Tools[toolNumber - 1];
                            double totalTimeSpent = preheatCounters[toolNumber] + timeSpent;
                            if (totalTimeSpent > (double)tool.PreheatTime)
                            {
                                // We've been doing enough stuff to generate a good G10 code
                                layer.Lines.Insert(lineIndex, new GCodeLine($"G10 P{toolNumber} R{tool.ActiveTemperature}"));
                                preheatCounters.Remove(toolNumber);
                            }
                            else
                            {
                                // Need to do some more...
                                preheatCounters[toolNumber] = totalTimeSpent;
                            }
                        }
                    }
                }

                progress.Report(iteration++);
            }

            // Override first generated G10 codes if we could not preheat in time
            if (preheatCounters.Count > 0 && layers.Count > 0)
            {
                foreach (GCodeLine line in layers[0].Lines)
                {
                    int? gCode = line.GetIValue('G');
                    if (gCode == 10)
                    {
                        int? pParam = line.GetIValue('P');
                        if (pParam != null && preheatCounters.ContainsKey(pParam.Value))
                        {
                            ToolSettings tool = settings.Tools[pParam.Value - 1];
                            line.Content = $"G10 P{pParam} R{tool.ActiveTemperature} S{tool.ActiveTemperature}";
                        }
                    }
                }
            }
        }

        public async Task WriteToFile(FileStream stream)
        {
            maxProgress.Report(layers.Count);

            StreamWriter sw = new StreamWriter(stream);
            for (int i = 0; i < layers.Count; i++)
            {
                foreach (GCodeLine line in layers[i].Lines)
                {
                    await sw.WriteLineAsync(line.Content);
                }
                progress.Report(i + 1);
            }
            sw.Flush();
        }
    }
}
