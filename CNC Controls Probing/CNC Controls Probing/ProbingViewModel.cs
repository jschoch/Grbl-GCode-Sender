﻿/*
 * ProbingViewModel.cs - part of CNC Probing library
 *
 * v0.19 / 2020-05-20 / Io Engineering (Terje Io)
 *
 */

/*

Copyright (c) 2020, Io Engineering (Terje Io)
All rights reserved.

Redistribution and use in source and binary forms, with or without modification,
are permitted provided that the following conditions are met:

· Redistributions of source code must retain the above copyright notice, this
list of conditions and the following disclaimer.

· Redistributions in binary form must reproduce the above copyright notice, this
list of conditions and the following disclaimer in the documentation and/or
other materials provided with the distribution.

· Neither the name of the copyright holder nor the names of its contributors may
be used to endorse or promote products derived from this software without
specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR
ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON
ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

*/

using System;
using System.Collections.Generic;
using System.Threading;
using System.Collections.ObjectModel;
using CNC.Core;
using CNC.GCode;

namespace CNC.Controls.Probing
{

    class Probing
    {
        public const string Command = "G38.3";
    }

    public class ProbingViewModel : ViewModelBase
    {
        public enum CoordMode
        {
            G10 = 0,
            G92
        }

        private string _message = string.Empty, _tool = string.Empty, _instructions = string.Empty;
        private double _tpHeight, _fHeight, _ProbeDiameter, _workpieceDiameter = 0d, _workpieceHeight = 0d;
        private double _latchDistance, _latchFeedRate;
        private double _probeDistance, _probeFeedRate;
        private double _offset, _xyClearance, _depth;

        private bool _canProbe = false, _isComplete = false, _isSuccess = false, _probeZ = false, _useFixture = false, _hasToolTable = false, _hasCs9 = false;
        private bool isCancelled = false, wasZselected = false;
        private GrblViewModel _grblmodel = null;
        private List<string> _program = new List<string>();
        private List<Position> _positions = new List<Position>();
        private List<Position> _machine = new List<Position>();
        private CoordMode _cmode = CoordMode.G10;
        private Edge _edge = Edge.None;
        private Center _center = Center.None;
        private int _coordinateSystem = 0;
        private ProbingProfile _profile;

        private CancellationToken cancellationToken = new CancellationToken();

        public Program Program;

        public ProbingViewModel (GrblViewModel grblmodel, ProbingProfiles profile)
        {
            Grbl = grblmodel;

//            Execute = new ActionCommand<bool>(ExecuteProgram);

            Program = new Program(this);

            Profiles = profile.Profiles;
            Profile = profile.Profiles[0];

            HeightMap.PropertyChanged += HeightMap_PropertyChanged;
        }

        private void HeightMap_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(HeightMapViewModel.HasHeightMap))
                HeightMap.CanApply = HeightMap.HasHeightMap && !HeightMapApplied && Grbl.IsFileLoaded;
        }

//        private ICommand Execute { get;  set; }

        public bool RemoveLastPosition()
        {
            bool ok;
            if ((ok = _positions.Count > 0))
                _positions.RemoveAt(_positions.Count - 1);

            return ok;
        }

        public bool WaitForResponse(string command)
        {
            bool? res = null;

            if (Grbl.ResponseLogVerbose)
                Grbl.ResponseLog.Add(command);

            new Thread(() =>
            {
                res = WaitFor.AckResponse<string>(
                cancellationToken,
                null,
                a => Grbl.OnResponseReceived += a,
                a => Grbl.OnResponseReceived -= a,
                5000, () => Grbl.ExecuteCommand(command));
            }).Start();

            while (res == null)
                EventUtils.DoEvents();

            return res == true;
        }

        public bool WaitForIdle(string command)
        {
            bool? res = null;

            if (Grbl.ResponseLogVerbose)
                Grbl.ResponseLog.Add(command);

            while (res == null)
            {
                new Thread(() =>
                {
                    res = WaitFor.SingleEvent<string>(
                    cancellationToken,
                    null,
                    a => Grbl.OnResponseReceived += a,
                    a => Grbl.OnResponseReceived -= a,
                    5000, () => Grbl.ExecuteCommand(command));
                }).Start();

                while (res == null)
                    EventUtils.DoEvents();

                if (Grbl.GrblState.State != GrblStates.Idle)
                    res = null;
            }

            return res == true;
        }

        public bool GotoMachinePosition(Position pos, AxisFlags axisflags)
        {
            bool? res = null;
            bool wait = true;

            string command = "G53G0" + pos.ToString(axisflags);

            Comms.com.PurgeQueue();

            new Thread(() =>
            {
                res = WaitFor.AckResponse<string>(
                cancellationToken,
                null,
                a => Grbl.OnResponseReceived += a,
                a => Grbl.OnResponseReceived -= a,
                100, () => Grbl.ExecuteCommand(command));
            }).Start();

            while (res == null)
                EventUtils.DoEvents();

            if (res == true) {

                while (wait && !isCancelled)
                {
                    res = null;

                    new Thread(() =>
                    {
                        res = WaitFor.SingleEvent<string>(
                        cancellationToken,
                        null,
                        a => Grbl.OnRealtimeStatusProcessed += a,
                        a => Grbl.OnRealtimeStatusProcessed -= a,
                        200, () => Comms.com.WriteByte(GrblLegacy.ConvertRTCommand(GrblConstants.CMD_STATUS_REPORT)));
                    }).Start();

                    while (res == null)
                        EventUtils.DoEvents();

                    wait = res != true;

                    int i = 0, axes = (int)axisflags;
                    while (axes != 0 && !wait)
                    {
                        if ((axes & 0x01) != 0)
                            wait = Math.Abs(pos.Values[i] - Grbl.MachinePosition.Values[i]) >= 0.003d; // use step resolution plus some?
                        i++; axes >>= 1;
                    }

                    if (wait)
                        Thread.Sleep(200); // needed?
                }
            }

            return isCancelled ? false : !wait;
        }

        public bool ValidateInput ()
        {
            ClearErrors();

            if(XYClearance > ProbeDistance)
            {
                SetError(nameof(XYClearance), "Probing distance must be larger than XY Clearance.");
                SetError(nameof(ProbeDistance), "Probing distance must be larger than XY Clearance.");
            }

            return !HasErrors;
        }

        public GrblViewModel Grbl { get { return _grblmodel; } private set { _grblmodel = value; OnPropertyChanged(); } }
        public HeightMapViewModel HeightMap { get; private set; } = new HeightMapViewModel();
        public ObservableCollection<CoordinateSystem> CoordinateSystems { get; private set; } = new ObservableCollection<CoordinateSystem>();
        public ObservableCollection<ProbingProfile> Profiles { get; private set; }
        public ProbingProfile Profile
        {
            get { return _profile; }
            set
            {
                _profile = value;
                ProbeFeedRate = _profile.ProbeFeedRate;
                LatchFeedRate = _profile.LatchFeedRate;
                ProbeDistance = _profile.ProbeDistance;
                LatchDistance = _profile.LatchDistance;
                ProbeDiameter = _profile.ProbeDiameter;
                Offset = _profile.Offset;
                XYClearance = _profile.XYClearance;
                Depth = _profile.Depth;
                TouchPlateHeight = _profile.TouchPlateHeight;
                FixtureHeight = _profile.FixtureHeight;
            }
        }

        public string FastProbe { get { return string.Format(Probing.Command + "F{0}", ProbeFeedRate.ToInvariantString()); } }
        public string SlowProbe { get { return string.Format(Probing.Command + "F{0}", LatchFeedRate.ToInvariantString()); } }
        public string Instructions { get { return _instructions; } set { _instructions = value; OnPropertyChanged(); } }

        public int CoordinateSystem { get { return _coordinateSystem; } set { _coordinateSystem = value; OnPropertyChanged(); } }
        public double ProbeFeedRate { get { return _probeFeedRate; } set { _probeFeedRate = value; OnPropertyChanged(); } }
        public double ProbeDistance { get { return _probeDistance; } set { _probeDistance = value; OnPropertyChanged(); } }
        public double ProbeDiameter { get { return _ProbeDiameter; } set { _ProbeDiameter = value; OnPropertyChanged(); } }
        public double LatchDistance { get { return _latchDistance; } set { _latchDistance = value; OnPropertyChanged(); } }
        public double LatchFeedRate { get { return _latchFeedRate; } set { _latchFeedRate = value; OnPropertyChanged(); } }
        public double TouchPlateHeight { get { return _tpHeight; } set { _tpHeight = value; OnPropertyChanged(); } }
        public double FixtureHeight { get { return _fHeight; } set { _fHeight = value; OnPropertyChanged(); } }
        public bool CanProbe { get { return _canProbe; } set { _canProbe = value; OnPropertyChanged(); } }
        public bool IsCompleted { get { return _isComplete; } set { _isComplete = value; OnPropertyChanged(); } }
        public bool IsSuccess { get { return _isSuccess; } set { _isSuccess = value; OnPropertyChanged(); } }
        public bool ProbeZ { get { return _probeZ; } set { _probeZ = value; OnPropertyChanged(); } }
        public string Tool { get { return _tool; } set { _tool = value; OnPropertyChanged(); } }
        public bool ProbeFixture { get { return _useFixture; } set { _useFixture = value; OnPropertyChanged(); } }
        public bool HasToolTable { get { return _hasToolTable; } set { _hasToolTable = value; OnPropertyChanged(); } }
        public bool HasCoordinateSystem9 { get { return _hasCs9; } set { _hasCs9 = value; OnPropertyChanged(); } }
        public bool HeightMapApplied
        {
            get { return GCode.File.HeightMapApplied; }
            set {
                if (GCode.File.HeightMapApplied != value) {
                    GCode.File.HeightMapApplied = value;
                    OnPropertyChanged();
                }
                HeightMap.CanApply = !GCode.File.HeightMapApplied;
            }
        }
//        public List<string> Program { get { return _program;  } }
        public List<Position> Positions { get { return _positions; } }
        public List<Position> Machine { get { return _machine; } }
        public double XYClearance { get { return _xyClearance; } set { _xyClearance = value; OnPropertyChanged(); } }
        public double Offset { get { return _offset; } set { _offset = value; OnPropertyChanged(); } }
        public double Depth { get { return _depth; } set { _depth = value; OnPropertyChanged(); } }

        public string Message
        {
            get { return _message; }
            set
            {
                _message = value; OnPropertyChanged();
                if (!string.IsNullOrEmpty(_message) && Grbl.ResponseLogVerbose)
                    Grbl.ResponseLog.Add(_message);
                Program.Silent = true;
                Grbl.Message = _message;
                Program.Silent = false;
            }
        }
        public CoordMode CoordinateMode { get { return _cmode; } set { _cmode = value; OnPropertyChanged(); } }

        public bool IsCoordinateModeG92 { get { return _cmode == CoordMode.G92; } set { _cmode = value ? CoordMode.G92 : CoordMode.G10; OnPropertyChanged(); OnPropertyChanged(nameof(CoordinateMode)); } }

        public Edge ProbeEdge
        {
            get { return _edge; }
            set
            {
                if(value == Edge.Z)
                {
                    if (!_probeZ && !ProbeZ)
                    {
                        wasZselected = true;
                        ProbeZ = true;
                    }
                }
                else if(_edge == Edge.Z)
                {
                    if (wasZselected)
                    {
                        wasZselected = false;
                        ProbeZ = false;
                    }
                }
                _edge = value; OnPropertyChanged();
            }
        }
        public Center ProbeCenter { get { return _center; } set { _center = value; OnPropertyChanged(); } }
        public double WorkpieceDiameter { get { return _workpieceDiameter; } set { _workpieceDiameter = value; OnPropertyChanged(); } }
        public double WorkpieceHeight { get { return _workpieceHeight; } set { _workpieceHeight = value; OnPropertyChanged(); } }

    }
}
