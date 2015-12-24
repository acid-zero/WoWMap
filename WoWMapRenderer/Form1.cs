﻿using System;
using System.Collections.Generic;

using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using CASCExplorer;
using WoWMap.Archive;
using WoWMap.Layers;
using OpenTK;
using WoWMap;
using OpenGL = OpenTK.Graphics.OpenGL;

namespace WoWMapRenderer
{
    public partial class Form1 : Form
    {
        private AsyncAction _cascAction;
        private AsyncAction _mapAction;
        private AsyncAction _adtAction;

        private string _wdtPath;
        private WDT _wdt;
        private Dictionary<int, ADT> _adts = new Dictionary<int, ADT>();
        private DBC<MapRecord> _mapRecords;
        private List<Renderer> _renderers = new List<Renderer>();

        private string _localCascPath = string.Empty;

        private Camera _camera;
        private Shader _terrainShader;

        #region Shaders body
        private const string TerrainFragmentShader = @"#version 330
 
out vec4 outputColor;
flat in int vert_type;

// Remember all code paths are always executed by GPU
void main()
{
    vec4 oColor = vec4(0.0f, 1.0f, 0.0f, 1.0f);
    if (vert_type == 1) // Doodad
        oColor = vec4(0.0f, 1.0f, 0.0f, 1.0f);
    else if (vert_type == 2) // WMO
        oColor = vec4(0.0f, 0.0f, 1.0f, 1.0f);
    outputColor = oColor;
}";

        private const string TerrainVertexShader = @"#version 330
 
in vec3 vPosition;
in int type;

uniform mat4 projection_modelview;
flat out int vert_type;

void main()
{
    gl_Position = projection_modelview * vec4(vPosition, 1.0f);
    vert_type = type;
}";
        #endregion


        public Form1()
        {
            InitializeComponent();
        }

        private void OnRenderLoaded(object sender, EventArgs e)
        {
            // TODO Move this
            _camera = new Camera(new Vector3(1731.5f, 1651.6f, 130.0f), Vector3.UnitY);

            _camera.SetViewport(GL.Width, GL.Height);
            OpenGL.GL.Viewport(0, 0, GL.Width, GL.Height);

            var uniform = Matrix4.Mult(_camera.Projection, _camera.View);

            // Setup shaders
            _terrainShader = new Shader();
            _terrainShader.CreateShader(TerrainVertexShader, TerrainFragmentShader);
            _terrainShader.SetCurrent();
            
            OpenGL.GL.UniformMatrix4(_terrainShader.GetUniformLocation("projection_modelview"), false, ref uniform);
            OpenGL.GL.ClearColor(Color.White);
        }

        private void OnLoad(object sender, EventArgs e)
        {
            _cascAction = new AsyncAction(() => {
                if (string.IsNullOrEmpty(_localCascPath))
                    CASC.InitializeOnline(_cascAction);
                else
                {
                    try {
                        CASC.Initialize(_localCascPath);
                    } catch (Exception ex) {
                        MessageBox.Show("Path '" + _localCascPath + "/Data' was not found.", "Error",
                            MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                    }
                }
            }, args =>
            {
                _feedbackText.Text = (string)args.UserData;
                _backgroundTaskProgress.Style = ProgressBarStyle.Continuous;
                _backgroundTaskProgress.Maximum = 100;
                _backgroundTaskProgress.Value = args.Progress;
            });

            _mapAction = new AsyncAction(() => {
                _cascAction.ReportProgress(0, "Loading maps ...");

                _mapRecords = new DBC<MapRecord>(@"DBFilesClient\Map.dbc");

                _mapListBox.Invoke(new Action(() =>
                {
                    var rowIndex = 0;
                    foreach (var mapEntry in _mapRecords.Rows)
                    {
                        ++rowIndex;
                        _mapAction.ReportProgress(rowIndex * 100 / _mapRecords.Rows.Length);

                        _mapListBox.Items.Add(new MapListBoxEntry
                        {
                            Name = mapEntry.MapNameLang,
                            Directory = mapEntry.Directory
                        });
                    }
                }));
            }, args =>
            {
                _feedbackText.Text = (string)args.UserData;
                _backgroundTaskProgress.Style = ProgressBarStyle.Continuous;
                _backgroundTaskProgress.Maximum = 100;
                _backgroundTaskProgress.Value = args.Progress;
            });

            _adtAction = new AsyncAction(() =>
            {
                try
                {
                    _wdt = new WDT(_wdtPath);
                    if (_wdt.IsGlobalModel)
                        return; // NYI

                    var tileCount = _wdt.TileCount;
                    var tileIndex = 0;
                    for (var i = 0; i < 64; ++i)
                    {
                        for (var j = 0; j < 64; ++j)
                        {
                            if (_wdt.HasTile(i, j))
                            {
                                _adts.Add((i << 8) | j, new ADT(Path.GetFileNameWithoutExtension(_wdtPath), i, j));
                                ++tileIndex;
                            }
                            _adtAction.ReportProgress(tileIndex * 100 / tileCount, "Loading ADTs ...");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _adtAction.ReportProgress(100, "Error when loading ADTs ...");
                }
            }, args =>
            {
                _feedbackText.Text = (string)args.UserData;
                _backgroundTaskProgress.Style = ProgressBarStyle.Continuous;
                _backgroundTaskProgress.Maximum = 100;
                _backgroundTaskProgress.Value = args.Progress;
            });
        }

        private void FilterChanged(object sender, EventArgs e)
        {
            // NYI
        }

        private async void MapSelected(object sender, EventArgs e)
        {
            var entry = (MapListBoxEntry)_mapListBox.Items[_mapListBox.SelectedIndex];
            _backgroundTaskProgress.Style = ProgressBarStyle.Marquee;

            _wdtPath = string.Format(@"World\Maps\{0}\{0}.wdt", entry.Directory);
            await _adtAction.DoAction();
            _feedbackText.Text = string.Format("{0} ADTs loaded!", _adts.Count);

            LoadMap();
            int centerX, centerY;
            GetCenterADT(out centerX, out centerY);
            var tileCenter = GetTileCenter(centerX, centerY);
            _camera = new Camera(new Vector3(tileCenter.X, tileCenter.Y, 150.0f), -Vector3.UnitZ);
            Render();
        }

        private void Render()
        {
            OpenGL.GL.Clear(OpenGL.ClearBufferMask.ColorBufferBit | OpenGL.ClearBufferMask.DepthBufferBit);

            _camera.SetViewport(GL.Width, GL.Height);
            var uniform = Matrix4.Mult(_camera.View, _camera.Projection);

            OpenGL.GL.UniformMatrix4(_terrainShader.GetUniformLocation("projection_modelview"), false, ref uniform);

            // Camera set - Clean again, to be safe
            // OpenGL.GL.Clear(OpenGL.ClearBufferMask.ColorBufferBit | OpenGL.ClearBufferMask.DepthBufferBit);

            OpenGL.GL.PolygonMode(OpenGL.MaterialFace.FrontAndBack, OpenGL.PolygonMode.Line);
            OpenGL.GL.Enable(OpenGL.EnableCap.CullFace);
            OpenGL.GL.Enable(OpenGL.EnableCap.DepthTest);
            OpenGL.GL.DepthFunc(OpenGL.DepthFunction.Less);

            foreach (var renderer in _renderers)
            {
                OpenGL.GL.BindVertexArray(renderer.VAO);
                OpenGL.GL.BindBuffer(OpenGL.BufferTarget.ElementArrayBuffer, renderer.IndiceVBO);
                _terrainShader.SetCurrent();
                OpenGL.GL.DrawElements(OpenGL.PrimitiveType.Triangles, renderer.TriangleCount,
                    OpenGL.DrawElementsType.UnsignedInt, IntPtr.Zero);
            }

            OpenGL.GL.BindVertexArray(0);

            GL.SwapBuffers();
        }

        private void LoadMap()
        {
            int centerX, centerY;
            GetCenterADT(out centerX, out centerY);
            for (var x = centerX - 1; x <= centerX + 1; ++x)
                for (var y = centerY - 1; y <= centerY + 1; ++y)
                    LoadADT(_adts[(x << 8) | y]);
        }

        private Vector2 GetTileCenter(int x, int y)
        {
            var adt = _adts[(x << 8) | y];
            var tilePosition = adt.TilePosition.Xy;
            tilePosition.X += Constants.TileSize / 2;
            tilePosition.Y += Constants.TileSize / 2;
            return tilePosition;
        }

        private void GetCenterADT(out int x, out int y)
        {
            var topLeft = new [] { 64, 64 };
            var bottomRight = new[] { 0, 0 };
            for (var xx = 0; xx < 64; ++xx)
            {
                for (var yy = 0; yy < 64; ++yy)
                {
                    if (!_wdt.HasTile(xx, yy))
                        continue;

                    topLeft[0] = Math.Min(topLeft[0], xx);
                    topLeft[1] = Math.Min(topLeft[1], yy);
                    bottomRight[0] = Math.Max(bottomRight[0], xx);
                    bottomRight[1] = Math.Max(bottomRight[1], yy);
                }
            }

            x = (int)Math.Floor((topLeft[0] + bottomRight[0]) / 2.0f);
            y = (int)Math.Floor((topLeft[1] + bottomRight[1]) / 2.0f);
        }

        private void LoadADT(ADT currentADT)
        {
            currentADT.Read();

            var verticeList = new List<VertexData>();
            var indiceList = new List<uint>();

            foreach (var adtChunk in currentADT.MapChunks)
            {
                if (adtChunk == null)
                    continue;

                var off = (uint) verticeList.Count();

                // Generate vertices
                for (int i = 0, idx = 0; i < 17; ++i)
                {
                    var maxJ = ((i%2) != 0) ? 8 : 9;
                    for (var j = 0; j < maxJ; j++)
                    {
                        if (adtChunk.MCCV != null)
                        {
                            verticeList.Add(new VertexData
                            {
                                Normal = adtChunk.MCNR.Entries[idx].Normal,
                                Color = new Vector3(adtChunk.MCCV.Entries[idx].Blue/127.0f,
                                    adtChunk.MCCV.Entries[idx].Green/127.0f, adtChunk.MCCV.Entries[idx].Red/127.0f),
                                Position = adtChunk.Vertices[idx],
                                // TextureCoordinates = ...
                            });
                        }
                        else
                        {
                            verticeList.Add(new VertexData
                            {
                                Normal = adtChunk.MCNR.Entries[idx].Normal,
                                Color = new Vector3(1.0f, 1.0f, 1.0f),
                                Position = adtChunk.Vertices[idx],
                                // TextureCoordinates = ...
                            });
                        }
                        ++idx;
                    }
                }

                // Generate indices
                foreach (var triangle in adtChunk.Indices)
                    indiceList.AddRange(new[] {triangle.V0 + off, triangle.V1 + off, triangle.V2 + off});
            }

            BindIndexed(verticeList, indiceList);

            verticeList.Clear();
            indiceList.Clear();

            #region Doodads
            foreach (var adtChunk in currentADT.Objects.MapChunks)
            {
                var off = (uint) verticeList.Count();

                if (adtChunk.DoodadVertices == null)
                    continue;

                verticeList.AddRange(adtChunk.DoodadVertices.Select((t, i) => new VertexData
                {
                    Color = new Vector3(1.0f, 1.0f, 1.0f),
                    Normal = adtChunk.DoodadNormals[i],
                    Position = t
                }));

                foreach (var triangle in adtChunk.DoodadIndices)
                    indiceList.AddRange(new[] { triangle.V0 + off, triangle.V1 + off, triangle.V2 + off });
            }
            BindIndexed(verticeList, indiceList, 1);
            #endregion

            verticeList.Clear();
            indiceList.Clear();

            #region WMO
            foreach (var adtChunk in currentADT.Objects.MapChunks)
            {
                var off = (uint)verticeList.Count();

                if (adtChunk.WMONormals == null)
                    continue;

                verticeList.AddRange(adtChunk.WMOVertices.Select((t, i) => new VertexData
                {
                    Color = new Vector3(1.0f, 1.0f, 1.0f),
                    Normal = adtChunk.WMONormals[i],
                    Position = t
                }));

                foreach (var triangle in adtChunk.WMOIndices)
                    indiceList.AddRange(new[] { triangle.V0 + off, triangle.V1 + off, triangle.V2 + off });
            }

            BindIndexed(verticeList, indiceList, 2);

            #endregion

            verticeList.Clear();
            indiceList.Clear();

            OpenGL.GL.ClearColor(OpenTK.Graphics.Color4.White);
        }

        private void BindIndexed(IReadOnlyCollection<VertexData> verticeList, List<uint> indiceList, int elementType = 0)
        {
            var renderer = new Renderer
            {
                IndiceVBO = OpenGL.GL.GenBuffer(),
                VertexVBO = OpenGL.GL.GenBuffer(),
                VAO = OpenGL.GL.GenVertexArray(),
                TriangleCount = indiceList.Count()
            };

            OpenGL.GL.BindVertexArray(renderer.VAO);

            var vertices = new Vertex[verticeList.Count];
            verticeList.Each((t, i) => vertices[i] = new Vertex
            {
                Color = t.Color,
                Position = t.Position,
                Normal = t.Normal,
                Type = elementType
            });
            
            var vertexSize = Marshal.SizeOf(typeof(Vertex));
            var verticeSize = verticeList.Count * vertexSize;

            OpenGL.GL.BindBuffer(OpenGL.BufferTarget.ArrayBuffer, renderer.VertexVBO);
            OpenGL.GL.BufferData(OpenGL.BufferTarget.ArrayBuffer, (IntPtr)(verticeSize),
                vertices, OpenGL.BufferUsageHint.StaticDraw);

            OpenGL.GL.VertexAttribPointer(_terrainShader.GetAttribLocation("vPosition"), 3,
                OpenGL.VertexAttribPointerType.Float, false, vertexSize, sizeof(float) * 6);
            OpenGL.GL.EnableVertexAttribArray(_terrainShader.GetAttribLocation("vPosition"));

            OpenGL.GL.VertexAttribIPointer(_terrainShader.GetAttribLocation("type"), 1,
                OpenGL.VertexAttribIntegerType.Int, vertexSize, (IntPtr)(sizeof(float) * 9));
            OpenGL.GL.EnableVertexAttribArray(_terrainShader.GetAttribLocation("type"));

            /*OpenGL.GL.VertexAttribIPointer(_terrainShader.GetAttribLocation("type"), 1,
                OpenGL.VertexAttribIPointerType.Int, vertexSize, sizeof(float) * 9);
            OpenGL.GL.EnableVertexAttribIPointer(_terrainShader.GetAttribLocation("type"));*/

            OpenGL.GL.BindBuffer(OpenGL.BufferTarget.ElementArrayBuffer, renderer.IndiceVBO);
            OpenGL.GL.BufferData(OpenGL.BufferTarget.ElementArrayBuffer, (IntPtr)(indiceList.Count() * sizeof(uint)),
                indiceList.ToArray(), OpenGL.BufferUsageHint.StaticDraw);

            _renderers.Add(renderer);
        }

        private async void LoadOnlineCASC(object sender, EventArgs e)
        {
            _localCascPath = string.Empty;
            await _cascAction.DoAction();
            if (CASC.Initialized)
                await _mapAction.DoAction();
        }

        private async void LoadLocalCASC(object sender, EventArgs e)
        {
            var dialog = new FolderBrowserDialog
            {
                Description = "Indicate the path to your World of Warcraft installation.",
                ShowNewFolderButton = false
            };
            if (dialog.ShowDialog() != DialogResult.OK)
                return;

            _localCascPath = dialog.SelectedPath;
            await _cascAction.DoAction();
            if (CASC.Initialized)
                await _mapAction.DoAction();
        }

        private void OnKeyPress(object sender, System.Windows.Forms.KeyPressEventArgs e)
        {
            _camera.Update();
            _cameraPos.Text = string.Format("Camera [ {0} {1} {2} ] Facing [ {3} {4} ]", _camera.Position.X, _camera.Position.Y,
                _camera.Position.Z, _camera.Pitch, _camera.Yaw);
        }

        private void OnRenderResize(object sender, EventArgs e)
        {
            OpenGL.GL.Viewport(0, 0, GL.Width, GL.Height);
            if (_camera != null)
            {
                _camera.SetViewport(GL.Width, GL.Height);
                Render();
            }
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            _camera.Update();
            _cameraPos.Text = string.Format("Camera [ {0} {1} {2} ] Facing [ {3} {4} ]", _camera.Position.X, _camera.Position.Y,
                _camera.Position.Z, _camera.Pitch, _camera.Yaw);
            Render();
        }

        private void OnPaint(object sender, PaintEventArgs e)
        {
            Render();
        }
    }

    internal struct MapListBoxEntry
    {
        public string Name;
        public string Directory;

        public override string ToString()
        {
            return Name;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VertexData
    {
        public Vector3 Normal;
        public Vector3 Color;
        public Vector3 Position;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Vertex
    {
        public Vector3 Normal;
        public Vector3 Color;
        public Vector3 Position;
        // public Vector2 TextureCoordinates;
        public int Type;
    }

    internal struct Renderer
    {
        public int IndiceVBO;
        public int VertexVBO;
        public int VAO;
        public int TriangleCount;
    }
}
