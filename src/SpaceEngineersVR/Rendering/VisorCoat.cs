// VisorCoat: what lies on the visor's glass: frost, mist, rain, snow and dust on its outside, and the fingertip's wipes through them.
using System;
using System.Threading.Tasks;
using VRageMath;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// The outside of the visor's glass, over the helmet's opening (<see cref="HelmetShell.Side"/> round each way,
    /// <see cref="HelmetShell.Up"/> up and <see cref="HelmetShell.Down"/> down, a little past its edge), and what lies on it:
    /// <list type="bullet">
    /// <item>Frost: feathered ice crystals grown in from the opening's edge along their branches as far as the cold has reached
    /// (<see cref="Step"/>'s frost reach, in shares of the way from the edge to the middle), over a granular rime at the rim and a
    /// faint haze between the feathers; scraped off, it grows back slowly while the reach lasts, and melts tips first as the reach
    /// draws back.</item>
    /// <item>Mist, condensed over the whole glass at once and gone as its cause (the glass colder than the air) goes.</item>
    /// <item>Water: raindrops of mixed sizes beading where they land, merging where they touch, and the heavier sliding down the
    /// glass the way gravity pulls it in fits and starts, gathering what they meet and leaving a thin wet trail and a line of tiny
    /// droplets behind; they dry slowly.</item>
    /// <item>Snow: flakes landing at random; in the middle they melt within a second or two, toward the edge they settle.</item>
    /// <item>Dust: a coat thickening, unevenly, the longer a storm blows; it stays until wiped, and rain washes it.</item>
    /// </list>
    /// A fingertip's stroke (<see cref="Wipe"/>) clears all of them along its path, each as much as a glove would, pushing water to
    /// the stroke's sides and leaving a streaked, smeared film along the arc that dries in seconds.
    /// <para>
    /// The spread-out things (frost's reach, mist, snow, dust, the wet film) are worked out on a coarse grid (<see cref="Width"/> by
    /// <see cref="Height"/>, about 0.65 deg a cell); the picture (<see cref="Compose"/>) is <see cref="Detail"/> times finer each way
    /// (about 0.22 deg a texel), with the frost's crystals, the drops, the trails and the smears drawn at that detail, so beads are
    /// round and crystals fine in the headset rather than squares of the grid. Only the parts of the picture whose grid has changed
    /// are worked out again each step. Premultiplied RGBA, its light to be scaled by the light on the glass where it is drawn.
    /// </para>
    /// The crystals' pattern is grown once, off the game thread (<see cref="Prepare"/>). Allocates nothing after it is made. One
    /// thread at a time (the game thread).
    /// </summary>
    internal sealed class VisorCoat
    {
        /// <summary>The grid's cells round and down: about 0.65 deg each over the helmet's opening. The breath's picture is this size.</summary>
        public const int Width = 192, Height = 144;

        /// <summary>The picture's texels to a grid cell, each way.</summary>
        public const int Detail = 3;

        /// <summary>The picture's texels round and down: about 0.22 deg each.</summary>
        public const int PictureWidth = Width * Detail, PictureHeight = Height * Detail;

        /// <summary>How far past the opening's edge the picture reaches, as a share of the opening, so its soft edge is in it.</summary>
        public const float Margin = 1.04f;

        private const int Count = Width * Height, PictureCount = PictureWidth * PictureHeight;

        /// <summary>The wipe's half-width, grid cells: a gloved fingertip, about 1.5 cm across at the glass.</summary>
        public const float WipeRadius = 5.5f;

        /// <summary>The most drops on the glass, of them running at once, and the most trails and smears.</summary>
        private const int MostDrops = 900, MostRunning = 16, MostStreaks = 1024;

        /// <summary>A drop this big (radius, picture texels) or bigger runs down the glass; it stops when it has shrunk to <see cref="StopRadius"/>.</summary>
        private const float RunRadius = 3.4f, StopRadius = 2.5f;

        // ---- The grid: what lies on the glass, each cell 0 (nothing) to about 1 (a full coat). Water is the wet film, for washing dust.

        private readonly float[] frost = new float[Count], mist = new float[Count], water = new float[Count], snow = new float[Count], dust = new float[Count];

        // Fixed: how far each cell is in from the opening's edge (a share of the way to the middle, negative outside), and broad
        // patches for a dust coat that is thicker in places.
        private readonly float[] edge = new float[Count], patches = new float[Count];

        // What each cell held when its part of the picture was last worked out, and which parts are to be worked out again.
        private readonly float[] lastFrost = new float[Count], lastMist = new float[Count], lastSnow = new float[Count], lastDust = new float[Count];
        private readonly bool[] dirty = new bool[Count], blockShows = new bool[Count];
        private int blocksShowing;
        private bool allDirty = true;
        private float reach, composedReach;
        private Vector3 composedDust = new Vector3(-1f);

        // ---- The picture: the spread-out things, worked out where they change; the drops and streaks go over a copy each step.

        private readonly byte[] under = new byte[PictureCount * 4];
        private readonly Pattern pattern;

        // The still things over the spread-out ones (the drops not running, the trails), kept in tiles: a tile is drawn again only when
        // something in it changed; each step the picture is a copy of it with the trails, smears and running drops over.
        private const int Tile = 24, TilesAcross = PictureWidth / Tile, TilesDown = PictureHeight / Tile;
        private readonly byte[] still = new byte[PictureCount * 4];
        private readonly bool[] tileDirty = new bool[TilesAcross * TilesDown];

        // Drops (picture texels): place, radius, running, the phase of their fits and starts, and the way travelled since the last droplet left.
        private readonly float[] dropX = new float[MostDrops], dropY = new float[MostDrops], dropR = new float[MostDrops], dropPhase = new float[MostDrops], dropTravel = new float[MostDrops];
        private readonly float[] dropDrawnR = new float[MostDrops], trailX = new float[MostDrops], trailY = new float[MostDrops];
        private readonly bool[] dropRuns = new bool[MostDrops];
        private int drops;
        private Vector2 downWay;

        // Streaks (picture texels): a running drop's wet trail or a wipe's smear, each a straight piece from (x0, y0) to (x1, y1).
        private readonly float[] streakX0 = new float[MostStreaks], streakY0 = new float[MostStreaks], streakX1 = new float[MostStreaks], streakY1 = new float[MostStreaks];
        private readonly float[] streakWidth = new float[MostStreaks], streakAge = new float[MostStreaks], streakLife = new float[MostStreaks], streakSeed = new float[MostStreaks];
        private readonly bool[] streakSmear = new bool[MostStreaks];
        private int streakNext, streaksAlive;

        private uint random = 0x9E3779B9u;
        private float rainCarry, snowCarry;

        private static Task<Pattern> growing;

        /// <summary>Starts growing the crystals' pattern off the game thread, once; <see cref="Ready"/> says when it is done.</summary>
        public static void Prepare()
        {
            if (growing == null)
                growing = Task.Run(() => new Pattern());
        }

        /// <summary>The crystals' pattern is grown (or failed: making a coat then throws why).</summary>
        public static bool Ready => growing != null && growing.IsCompleted;

        /// <summary>A clean glass. Only once <see cref="Ready"/>.</summary>
        public VisorCoat()
        {
            pattern = growing.Result;
            for (int t = 0; t < tileDirty.Length; t++)
                tileDirty[t] = true;
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    int i = y * Width + x;
                    Angles((x + 0.5f) / Width, (y + 0.5f) / Height, out float round, out float up);
                    edge[i] = 1f - HelmetShell.OpeningRadius(round, up);
                    patches[i] = Noise(x * 0.05f + 3f, y * 0.05f + 11f) * 0.7f + Noise(x * 0.13f + 9f, y * 0.13f) * 0.3f;
                }
            }
        }

        /// <summary>
        /// The direction through the picture at <paramref name="u"/> across and <paramref name="v"/> down (0 to 1), as angles from
        /// straight ahead in the helmet's space (radians, <see cref="HelmetShell.Direction"/>). Pure.
        /// </summary>
        public static void Angles(float u, float v, out float round, out float up)
        {
            round = MathHelper.Lerp(-HelmetShell.Side, HelmetShell.Side, u) * Margin;
            up = MathHelper.Lerp(HelmetShell.Up * Margin, -HelmetShell.Down * Margin, v);
        }

        /// <summary>Where a direction (in angles from straight ahead) falls in the picture, 0 to 1 across and down. Pure.</summary>
        public static Vector2 Place(float round, float up) =>
            new Vector2((round / (HelmetShell.Side * Margin) + 1f) * 0.5f, (HelmetShell.Up * Margin - up) / ((HelmetShell.Up + HelmetShell.Down) * Margin));

        /// <summary>Nothing lies on the glass.</summary>
        public void Clear()
        {
            Array.Clear(frost, 0, Count);
            Array.Clear(mist, 0, Count);
            Array.Clear(water, 0, Count);
            Array.Clear(snow, 0, Count);
            Array.Clear(dust, 0, Count);
            drops = 0;
            Array.Clear(streakLife, 0, MostStreaks);
            streaksAlive = 0;
            reach = 0f;
            allDirty = true;
            for (int t = 0; t < tileDirty.Length; t++)
                tileDirty[t] = true;
        }

        /// <summary>
        /// <paramref name="seconds"/> on the glass: frost grows toward <paramref name="frostReach"/> (shares of the way in from the
        /// edge; 0 melts it all), mist toward <paramref name="mistLevel"/> (0 to 1; it clears at once as that falls), rain, snow and
        /// a dust storm land as hard as their shares (0 to 1), water runs down along <paramref name="down"/> (the way gravity pulls,
        /// in the picture's texels across and down; zero: it stays), and with <paramref name="freezing"/> off settled snow melts.
        /// </summary>
        public void Step(float seconds, float frostReach, float mistLevel, float rain, float snowfall, float storm, Vector2 down, bool freezing)
        {
            reach = frostReach;
            float grow = 0.12f * seconds, melt = 0.2f * seconds, condense = 0.5f * seconds, dry = (freezing ? 0.008f : 0.02f) * seconds;
            float settle = 0.5f * seconds, thaw = freezing ? 0f : 0.03f * seconds;
            float coat = storm * 0.035f * seconds, wash = 0.15f * seconds;
            for (int i = 0; i < Count; i++)
            {
                // Frost: wherever the reach has come to, the crystals' pattern shows how far along them (Compose); scraped, it grows back.
                float target = frostReach <= 0f ? 0f : Smooth(-0.03f, 0.02f, frostReach + 0.03f - edge[i]);
                float f = frost[i];
                frost[i] = f < target ? Math.Min(target, f + grow) : Math.Max(target, f - melt);

                float m = mist[i];
                mist[i] = m < mistLevel ? Math.Min(mistLevel, m + condense) : mistLevel;

                float w = water[i];
                if (w > 0f)
                    water[i] = Math.Max(0f, w - dry);

                float s = snow[i];
                if (s > 0f)
                    snow[i] = Math.Max(0f, s - settle * Smooth(0.12f, 0.35f, edge[i]) - thaw);

                float d = dust[i];
                if (coat > 0f && d < 0.85f)
                    d = Math.Min(0.85f, d + coat * (0.4f + 1.2f * patches[i]));
                if (d > 0f && w > 0.1f)
                    d = Math.Max(0f, d - w * wash);
                dust[i] = d;
            }

            // Drops dry, small ones first.
            float shrink = (freezing ? 0.004f : 0.02f) * seconds;
            for (int k = 0; k < drops; k++)
            {
                if (dropRuns[k])
                    continue;
                float r = dropR[k] -= shrink;
                if (Math.Abs(r - dropDrawnR[k]) > 0.12f || r < 0.35f)
                {
                    MarkDrop(k, Math.Max(r, dropDrawnR[k]));
                    dropDrawnR[k] = r;
                }
            }

            rainCarry += rain * 70f * seconds;
            for (; rainCarry >= 1f; rainCarry -= 1f)
                Raindrop();
            snowCarry += snowfall * 45f * seconds;
            for (; snowCarry >= 1f; snowCarry -= 1f)
                Flake();
            Run(seconds, down);
            Settle();

            for (int k = 0; k < MostStreaks; k++)
            {
                if (streakLife[k] <= 0f)
                    continue;
                float age = streakAge[k], aged = age + seconds;
                streakAge[k] = aged;
                if (aged >= streakLife[k])
                {
                    if (!streakSmear[k])
                        MarkStreak(k);
                    streakLife[k] = 0f;
                    streaksAlive--;
                }
                else if (!streakSmear[k] && (int)(aged * 2f) != (int)(age * 2f))
                {
                    MarkStreak(k);
                }
            }
        }

        /// <summary>
        /// A fingertip's stroke from (<paramref name="u0"/>, <paramref name="v0"/>) to (<paramref name="u1"/>, <paramref name="v1"/>)
        /// in the picture (0 to 1), the <paramref name="stroke"/>th touch: everything along it cleared as far as a glove clears it,
        /// softly at the stroke's sides, the drops it meets pushed aside to a few droplets at its edges and a streaked film left
        /// along it. Returns how much it took off, in full cells' worth.
        /// </summary>
        public float Wipe(float u0, float v0, float u1, float v1, int stroke)
        {
            float ax = u0 * Width, ay = v0 * Height, bx = u1 * Width, by = v1 * Height;
            int left = Math.Max(0, (int)(Math.Min(ax, bx) - WipeRadius - 1f)), right = Math.Min(Width - 1, (int)(Math.Max(ax, bx) + WipeRadius + 1f));
            int top = Math.Max(0, (int)(Math.Min(ay, by) - WipeRadius - 1f)), bottom = Math.Min(Height - 1, (int)(Math.Max(ay, by) + WipeRadius + 1f));
            float dx = bx - ax, dy = by - ay, length2 = dx * dx + dy * dy;
            float taken = 0f;
            for (int y = top; y <= bottom; y++)
            {
                for (int x = left; x <= right; x++)
                {
                    float px = x + 0.5f - ax, py = y + 0.5f - ay;
                    float t = length2 > 1e-6f ? MathHelper.Clamp((px * dx + py * dy) / length2, 0f, 1f) : 0f;
                    float ex = px - t * dx, ey = py - t * dy;
                    float distance = (float)Math.Sqrt(ex * ex + ey * ey);
                    float k = Smooth(WipeRadius, WipeRadius * 0.55f, distance);
                    if (k <= 0f)
                        continue;
                    int i = y * Width + x;
                    taken += Take(frost, i, 0.9f * k) + Take(mist, i, 0.97f * k) + Take(water, i, 0.6f * k) + Take(snow, i, 0.95f * k) + Take(dust, i, 0.75f * k);
                }
            }

            // In the picture: the drops it meets pushed to its edges, the trails it crosses gone, older smears under it fading, and its own smear.
            float pax = u0 * PictureWidth, pay = v0 * PictureHeight, pbx = u1 * PictureWidth, pby = v1 * PictureHeight;
            float pdx = pbx - pax, pdy = pby - pay, plength2 = pdx * pdx + pdy * pdy, plength = (float)Math.Sqrt(plength2);
            float reachOf = WipeRadius * Detail;
            float sideX = plength > 0.01f ? -pdy / plength : 0f, sideY = plength > 0.01f ? pdx / plength : 1f;
            int alive = drops;
            for (int k = 0; k < alive; k++)
            {
                float r = dropR[k];
                if (r <= 0f)
                    continue;
                float px = dropX[k] - pax, py = dropY[k] - pay;
                float t = plength2 > 1e-4f ? MathHelper.Clamp((px * pdx + py * pdy) / plength2, 0f, 1f) : 0f;
                float ex = px - t * pdx, ey = py - t * pdy;
                if (ex * ex + ey * ey > (reachOf * 0.9f) * (reachOf * 0.9f))
                    continue;
                Remove(k);
                taken += r * r * 0.12f;
                if (Next() < 0.45f && r > 0.8f)
                {
                    float side = ex * sideX + ey * sideY >= 0f ? 1f : -1f;
                    Add(pax + t * pdx + sideX * side * reachOf * (0.92f + 0.12f * Next()), pay + t * pdy + sideY * side * reachOf * (0.92f + 0.12f * Next()), 0.5f + 0.45f * r * Next());
                }
            }
            for (int k = 0; k < MostStreaks; k++)
            {
                if (streakLife[k] <= 0f)
                    continue;
                float mx = (streakX0[k] + streakX1[k]) * 0.5f - pax, my = (streakY0[k] + streakY1[k]) * 0.5f - pay;
                float t = plength2 > 1e-4f ? MathHelper.Clamp((mx * pdx + my * pdy) / plength2, 0f, 1f) : 0f;
                float ex = mx - t * pdx, ey = my - t * pdy, e2 = ex * ex + ey * ey;
                if (!streakSmear[k] && e2 < reachOf * reachOf)
                {
                    streakAge[k] = streakLife[k] * 0.999f;
                    MarkStreak(k);
                }
                else if (streakSmear[k] && e2 < reachOf * reachOf * 0.36f)
                    streakAge[k] = Math.Max(streakAge[k], streakLife[k] * 0.7f);
            }
            if (plength > 0.75f)
                AddStreak(pax, pay, pbx, pby, reachOf, 9f, smear: true, seed: (stroke & 255) * 7.31f);
            Settle();
            return taken;
        }

        private static float Take(float[] field, int i, float share)
        {
            float before = field[i];
            if (before <= 0f)
                return 0f;
            field[i] = before * (1f - share);
            return before * share;
        }

        /// <summary>
        /// The picture of what lies on the glass into <paramref name="texels"/> (RGBA, premultiplied, <see cref="PictureWidth"/> by
        /// <see cref="PictureHeight"/>, row by row), dust in <paramref name="dustColour"/>; whether any of it shows.
        /// </summary>
        public bool Compose(byte[] texels, Vector3 dustColour)
        {
            // Which cells have changed since their part of the picture was worked out; those and the cells round them are worked out again.
            bool all = allDirty || Vector3.DistanceSquared(dustColour, composedDust) > 0.0004f;
            if (all)
                composedDust = dustColour;
            Array.Clear(dirty, 0, Count);
            for (int i = 0; i < Count; i++)
            {
                if (!all && Math.Abs(frost[i] - lastFrost[i]) <= 0.025f && Math.Abs(mist[i] - lastMist[i]) <= 0.01f
                    && Math.Abs(snow[i] - lastSnow[i]) <= 0.01f && Math.Abs(dust[i] - lastDust[i]) <= 0.01f)
                    continue;
                lastFrost[i] = frost[i];
                lastMist[i] = mist[i];
                lastSnow[i] = snow[i];
                lastDust[i] = dust[i];
                int cx = i % Width, cy = i / Width;
                for (int ny = Math.Max(0, cy - 1); ny <= Math.Min(Height - 1, cy + 1); ny++)
                {
                    for (int nx = Math.Max(0, cx - 1); nx <= Math.Min(Width - 1, cx + 1); nx++)
                        dirty[ny * Width + nx] = true;
                }
            }
            bool reachMoved = Math.Abs(reach - composedReach) > 0.002f || (reach <= 0f) != (composedReach <= 0f);
            float reachLow = Math.Min(reach, composedReach) - 0.012f, reachHigh = Math.Max(reach, composedReach) + 0.01f;
            if (reachMoved)
                composedReach = reach;
            for (int c = 0; c < Count; c++)
            {
                if (dirty[c] || (reachMoved && frost[c] > 0f && pattern.CellHigh[c] >= reachLow && pattern.CellLow[c] <= reachHigh))
                    ComposeBlock(c % Width, c / Width, composedDust, all);
            }
            allDirty = false;

            // The tiles that changed drawn again: the spread-out things, the trails over them, the still drops over those.
            bool anyTile = false;
            for (int t = 0; t < tileDirty.Length; t++)
            {
                if (!tileDirty[t])
                    continue;
                anyTile = true;
                int left = (t % TilesAcross) * Tile, top = (t / TilesAcross) * Tile;
                for (int y = top; y < top + Tile; y++)
                    Buffer.BlockCopy(under, (y * PictureWidth + left) * 4, still, (y * PictureWidth + left) * 4, Tile * 4);
            }
            if (anyTile)
            {
                for (int k = 0; k < MostStreaks; k++)
                {
                    if (streakLife[k] <= 0f || streakSmear[k])
                        continue;
                    float w = streakWidth[k] + 1f;
                    DrawInTiles(k, false, Math.Min(streakX0[k], streakX1[k]) - w, Math.Min(streakY0[k], streakY1[k]) - w,
                        Math.Max(streakX0[k], streakX1[k]) + w, Math.Max(streakY0[k], streakY1[k]) + w);
                }
                for (int k = 0; k < drops; k++)
                {
                    if (dropRuns[k] || dropR[k] <= 0f)
                        continue;
                    float reachOf = dropR[k] + 2f;
                    DrawInTiles(k, true, dropX[k] - reachOf, dropY[k] - reachOf, dropX[k] + reachOf, dropY[k] + reachOf);
                }
                Array.Clear(tileDirty, 0, tileDirty.Length);
            }

            Buffer.BlockCopy(still, 0, texels, 0, still.Length);
            for (int k = 0; k < MostStreaks; k++)
            {
                if (streakLife[k] > 0f && streakSmear[k])
                    DrawStreak(texels, k, 0, 0, PictureWidth - 1, PictureHeight - 1);
            }
            for (int k = 0; k < drops; k++)
            {
                if (dropRuns[k] && dropR[k] > 0f)
                    DrawDrop(texels, k, 0, 0, PictureWidth - 1, PictureHeight - 1);
            }
            return blocksShowing > 0 || drops > 0 || streaksAlive > 0;
        }

        /// <summary>Drop or trail <paramref name="k"/>, over the stretch of the picture given, into those of its tiles drawn again this step.</summary>
        private void DrawInTiles(int k, bool drop, float left, float top, float right, float bottom)
        {
            int tx0 = Math.Max(0, (int)Math.Max(0f, left) / Tile), tx1 = Math.Min(TilesAcross - 1, (int)Math.Max(0f, right) / Tile);
            int ty0 = Math.Max(0, (int)Math.Max(0f, top) / Tile), ty1 = Math.Min(TilesDown - 1, (int)Math.Max(0f, bottom) / Tile);
            for (int ty = ty0; ty <= ty1; ty++)
            {
                for (int tx = tx0; tx <= tx1; tx++)
                {
                    if (!tileDirty[ty * TilesAcross + tx])
                        continue;
                    int x0 = tx * Tile, y0 = ty * Tile;
                    if (drop)
                        DrawDrop(still, k, x0, y0, x0 + Tile - 1, y0 + Tile - 1);
                    else
                        DrawStreak(still, k, x0, y0, x0 + Tile - 1, y0 + Tile - 1);
                }
            }
        }

        /// <summary>The spread-out things over one cell's texels of the picture, from the grid between the cells round it.</summary>
        private void ComposeBlock(int cx, int cy, Vector3 dustColour, bool all)
        {
            int cell = cy * Width + cx;
            if (all && !blockShows[cell])
            {
                // Nothing on it or round it, and nothing shown there: as it was.
                float held = 0f;
                for (int ny = Math.Max(0, cy - 1); ny <= Math.Min(Height - 1, cy + 1); ny++)
                {
                    for (int nx = Math.Max(0, cx - 1); nx <= Math.Min(Width - 1, cx + 1); nx++)
                    {
                        int n = ny * Width + nx;
                        held += frost[n] + mist[n] + snow[n] + dust[n];
                    }
                }
                if (held <= 0f)
                    return;
            }
            bool any = false;
            for (int yy = 0; yy < Detail; yy++)
            {
                int py = cy * Detail + yy;
                float sy = (py + 0.5f) / Detail - 0.5f;
                int y0 = (int)Math.Floor(sy);
                float fy = sy - y0;
                int y1 = y0 + 1;
                if (y0 < 0) { y0 = 0; fy = 0f; }
                if (y1 > Height - 1) { y1 = Height - 1; }
                for (int xx = 0; xx < Detail; xx++)
                {
                    int px = cx * Detail + xx;
                    int i = py * PictureWidth + px, at = i * 4;
                    float g = pattern.Glass[i];
                    if (g <= 0f)
                    {
                        under[at] = under[at + 1] = under[at + 2] = under[at + 3] = 0;
                        continue;
                    }
                    float sx = (px + 0.5f) / Detail - 0.5f;
                    int x0 = (int)Math.Floor(sx);
                    float fx = sx - x0;
                    int x1 = x0 + 1;
                    if (x0 < 0) { x0 = 0; fx = 0f; }
                    if (x1 > Width - 1) { x1 = Width - 1; }
                    int a = y0 * Width + x0, b = y0 * Width + x1, c = y1 * Width + x0, d = y1 * Width + x1;
                    float w00 = (1f - fx) * (1f - fy), w10 = fx * (1f - fy), w01 = (1f - fx) * fy, w11 = fx * fy;
                    float grain = pattern.Grain[i];
                    float r = 0f, gr = 0f, bl = 0f, al = 0f;

                    float m = mist[a] * w00 + mist[b] * w10 + mist[c] * w01 + mist[d] * w11;
                    if (m > 0.002f)
                        Over(ref r, ref gr, ref bl, ref al, 0.8f, 0.82f, 0.84f, 0.7f * m * (0.85f + 0.3f * grain));

                    // Frost: the crystals' pattern, each texel showing once the reach has come along its branch to it.
                    float f = frost[a] * w00 + frost[b] * w10 + frost[c] * w01 + frost[d] * w11;
                    if (f > 0.002f)
                    {
                        float ice = pattern.Ice[i];
                        if (ice > 0.005f)
                        {
                            float arrive = pattern.Arrival[i];
                            float shows = Smooth(arrive - 0.006f, arrive + 0.01f, composedReach);
                            if (shows > 0f)
                                Over(ref r, ref gr, ref bl, ref al, 0.84f + 0.12f * ice, 0.89f + 0.09f * ice, 0.97f + 0.03f * ice, f * shows * Math.Min(0.82f, ice * 0.82f));
                        }
                    }

                    float du = dust[a] * w00 + dust[b] * w10 + dust[c] * w01 + dust[d] * w11;
                    if (du > 0.002f)
                    {
                        float shade = 0.85f + 0.3f * grain;
                        Over(ref r, ref gr, ref bl, ref al, dustColour.X * shade, dustColour.Y * shade, dustColour.Z * shade, du * (0.8f + 0.2f * grain));
                    }
                    float s = snow[a] * w00 + snow[b] * w10 + snow[c] * w01 + snow[d] * w11;
                    if (s > 0.002f)
                        Over(ref r, ref gr, ref bl, ref al, 0.93f, 0.95f, 0.98f, Math.Min(1f, s) * 0.95f);

                    al *= g;
                    under[at] = Byte(r * g);
                    under[at + 1] = Byte(gr * g);
                    under[at + 2] = Byte(bl * g);
                    byte alpha = Byte(al);
                    under[at + 3] = alpha;
                    if (alpha > 1)
                        any = true;
                }
            }
            tileDirty[(cy * Detail / Tile) * TilesAcross + cx * Detail / Tile] = true;
            if (any != blockShows[cell])
            {
                blockShows[cell] = any;
                blocksShowing += any ? 1 : -1;
            }
        }

        /// <summary>
        /// One drop over the picture: a bead that bends the light, its upper part dark (it shows the ground below, upside down)
        /// and its lower part light (the sky above), a dark rim where its edge turns the light away, and a glint up and to the
        /// left; a running drop drawn longer along its way down.
        /// </summary>
        private void DrawDrop(byte[] texels, int k, int clipLeft, int clipTop, int clipRight, int clipBottom)
        {
            float cx = dropX[k], cy = dropY[k], r = dropR[k];
            if (r <= 0f)
                return;
            float stretch = dropRuns[k] ? 1.3f : 1f, ux = downWay.X, uy = downWay.Y;
            float extent = r * stretch + 1.5f;
            int left = Math.Max(clipLeft, (int)(cx - extent)), right = Math.Min(clipRight, (int)(cx + extent));
            int top = Math.Max(clipTop, (int)(cy - extent)), bottom = Math.Min(clipBottom, (int)(cy + extent));
            float strength = Math.Min(1f, 0.35f + 0.55f * r);
            float glintSize = Math.Max(0.6f, 0.22f * r);
            for (int y = top; y <= bottom; y++)
            {
                for (int x = left; x <= right; x++)
                {
                    float px = x + 0.5f - cx, py = y + 0.5f - cy;
                    if (stretch > 1f)
                    {
                        // Along the way down, shortened back to a circle: the head (ahead) rounder, the tail longer.
                        float along = px * ux + py * uy;
                        float back = along * (1f - 1f / (along >= 0f ? 1.12f : stretch * 1.15f));
                        px -= back * ux;
                        py -= back * uy;
                    }
                    float distance = (float)Math.Sqrt(px * px + py * py);
                    int i = y * PictureWidth + x;
                    float cover = MathHelper.Clamp(r - distance + 0.6f, 0f, 1f) * pattern.Glass[i] * strength;
                    if (cover <= 0f)
                        continue;
                    int at = i * 4;
                    float q = distance / r, lower = MathHelper.Clamp(py / r, -1f, 1f);
                    Blend(texels, at, 0.07f, 0.075f, 0.085f, cover * (0.12f + 0.2f * Smooth(0.1f, -0.9f, lower)));
                    Blend(texels, at, 0.86f, 0.9f, 0.95f, cover * 0.4f * Smooth(-0.1f, 0.8f, lower) * Smooth(0.25f, 0.6f, q) * (1f - Smooth(0.78f, 0.98f, q)));
                    Blend(texels, at, 0.05f, 0.055f, 0.065f, cover * 0.36f * Smooth(0.62f, 0.98f, q));
                    float gx = px + 0.3f * r, gy = py + 0.36f * r;
                    float glint = 1f - Smooth(0.25f, 1f, (float)Math.Sqrt(gx * gx + gy * gy) / glintSize);
                    if (glint > 0f)
                        Blend(texels, at, 1f, 1f, 1f, cover * 0.85f * glint);
                }
            }
        }

        /// <summary>
        /// One streak over the picture: a running drop's thin wet trail, or a wipe's smear, a film streaked along the stroke by the
        /// glove and heavier at its sides where the water was pushed, both fading as they dry.
        /// </summary>
        private void DrawStreak(byte[] texels, int k, int clipLeft, int clipTop, int clipRight, int clipBottom)
        {
            float ax = streakX0[k], ay = streakY0[k], bx = streakX1[k], by = streakY1[k], width = streakWidth[k];
            float dx = bx - ax, dy = by - ay, length2 = dx * dx + dy * dy;
            if (length2 < 1e-4f)
                return;
            float length = (float)Math.Sqrt(length2), sx = -dy / length, sy = dx / length;
            bool smear = streakSmear[k];
            float fade = 1f - (smear ? streakAge[k] : (float)Math.Floor(streakAge[k] * 2f) * 0.5f) / streakLife[k];
            fade *= fade * (3f - 2f * fade);
            float seed = streakSeed[k];
            int left = Math.Max(clipLeft, (int)(Math.Min(ax, bx) - width - 1f)), right = Math.Min(clipRight, (int)(Math.Max(ax, bx) + width + 1f));
            int top = Math.Max(clipTop, (int)(Math.Min(ay, by) - width - 1f)), bottom = Math.Min(clipBottom, (int)(Math.Max(ay, by) + width + 1f));
            for (int y = top; y <= bottom; y++)
            {
                for (int x = left; x <= right; x++)
                {
                    float px = x + 0.5f - ax, py = y + 0.5f - ay;
                    float t = (px * dx + py * dy) / length2;
                    if (t < 0f || t >= 1f)
                        continue;
                    float e = px * sx + py * sy, across = Math.Abs(e);
                    int i = y * PictureWidth + x;
                    float g = pattern.Glass[i];
                    if (g <= 0f)
                        continue;
                    if (smear)
                    {
                        float share = across / width;
                        if (share >= 1f)
                            continue;
                        float profile = (0.5f + 0.5f * Smooth(0.4f, 0.93f, share)) * (1f - Smooth(0.9f, 1f, share));
                        float streaks = 0.35f + 0.65f * Noise(e * 0.85f + seed, seed * 0.37f);
                        float tone = Noise(e * 0.45f + seed * 1.7f, 5f) * 0.6f + 0.25f;
                        Blend(texels, i * 4, tone, tone + 0.02f, tone + 0.05f, g * fade * 0.55f * profile * streaks);
                    }
                    else
                    {
                        float cover = MathHelper.Clamp(width + 0.5f - across, 0f, 1f);
                        if (cover <= 0f)
                            continue;
                        Blend(texels, i * 4, 0.12f, 0.13f, 0.15f, g * fade * 0.22f * cover);
                        if (e < 0f)
                            Blend(texels, i * 4, 0.9f, 0.93f, 0.97f, g * fade * 0.25f * cover * Smooth(0f, width, across));
                    }
                }
            }
        }

        private void AddStreak(float x0, float y0, float x1, float y1, float width, float life, bool smear, float seed)
        {
            int k = streakNext;
            streakNext = (streakNext + 1) % MostStreaks;
            if (streakLife[k] <= 0f)
                streaksAlive++;
            else if (!streakSmear[k])
                MarkStreak(k);
            streakX0[k] = x0;
            streakY0[k] = y0;
            streakX1[k] = x1;
            streakY1[k] = y1;
            streakWidth[k] = width;
            streakAge[k] = 0f;
            streakLife[k] = life;
            streakSmear[k] = smear;
            streakSeed[k] = seed;
            if (!smear)
                MarkStreak(k);
        }

        /// <summary>The tiles a trail crosses are to be drawn again.</summary>
        private void MarkStreak(int k)
        {
            float w = streakWidth[k] + 2f;
            Mark(Math.Min(streakX0[k], streakX1[k]) - w, Math.Min(streakY0[k], streakY1[k]) - w, Math.Max(streakX0[k], streakX1[k]) + w, Math.Max(streakY0[k], streakY1[k]) + w);
        }

        /// <summary>
        /// The breath's mist on the inside of the glass, at its fullest, as a picture over the same place (<see cref="Width"/> by
        /// <see cref="Height"/>, RGBA, premultiplied): a soft cloud in the middle, rising from <paramref name="mouth"/> below level (radians:
        /// the foot of the view, where the mouth breathes against the glass), ragged at its top and cut off at the opening's edge. Pure.
        /// </summary>
        public static byte[] BreathTexels(float mouth)
        {
            var texels = new byte[Count * 4];
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    int at = (y * Width + x) * 4;
                    Angles((x + 0.5f) / Width, (y + 0.5f) / Height, out float round, out float up);
                    float e = 1f - HelmetShell.OpeningRadius(round, up);
                    if (e <= 0f)
                        continue;
                    // From the mouth: within about 40 deg either side of the middle, up to about 25 deg above it, ragged.
                    float across = round / MathHelper.ToRadians(40f), rise = (up + mouth) / MathHelper.ToRadians(25f);
                    float ragged = 0.25f * (Noise(x * 0.12f + 5f, y * 0.12f + 9f) - 0.5f);
                    float cloud = 1f - Smooth(0.4f, 1f, (float)Math.Sqrt(across * across + rise * rise) + ragged);
                    float a = 0.55f * cloud * (0.8f + 0.4f * Noise(x * 0.6f, y * 0.6f + 3f)) * MathHelper.Clamp(e / 0.015f, 0f, 1f);
                    texels[at] = Byte(0.85f * a);
                    texels[at + 1] = Byte(0.87f * a);
                    texels[at + 2] = Byte(0.9f * a);
                    texels[at + 3] = Byte(a);
                }
            }
            return texels;
        }

        /// <summary>One raindrop at random on the glass, mostly small, a few large; on another it merges, and a drop grown heavy starts to run.</summary>
        private void Raindrop()
        {
            float x = Next() * PictureWidth, y = Next() * PictureHeight;
            if (pattern.Edge[(int)y * PictureWidth + (int)x] < 0.005f)
                return;
            float u = Next();
            float r = 0.5f + 2.2f * u * u + 2.6f * u * u * u * u * u * u;
            int cell = (int)(y / Detail) * Width + (int)(x / Detail);
            water[cell] = Math.Min(1f, water[cell] + 0.15f);
            Add(x, y, r);
        }

        /// <summary>A drop at (x, y), radius r, merged with any it lands on (their water together, where the heavier was).</summary>
        private void Add(float x, float y, float r)
        {
            if (r < 0.35f || x < 0f || y < 0f || x >= PictureWidth || y >= PictureHeight || pattern.Edge[(int)y * PictureWidth + (int)x] < 0f)
                return;
            float volume = r * r * r;
            for (int k = 0; k < drops; k++)
            {
                float rk = dropR[k];
                if (rk <= 0f)
                    continue;
                float dx = dropX[k] - x, dy = dropY[k] - y, reachBoth = (rk + r) * 0.95f;
                if (dx * dx + dy * dy >= reachBoth * reachBoth)
                    continue;
                float vk = rk * rk * rk, share = vk / (vk + volume);
                x += dx * share;
                y += dy * share;
                volume += vk;
                Remove(k);
            }
            if (drops >= MostDrops)
            {
                Settle();
                if (drops >= MostDrops)
                    return;
            }
            int n = drops++;
            dropX[n] = x;
            dropY[n] = y;
            dropR[n] = dropDrawnR[n] = (float)Math.Pow(volume, 1.0 / 3.0);
            dropRuns[n] = false;
            dropPhase[n] = Next() * MathHelper.TwoPi;
            dropTravel[n] = 0f;
            MarkDrop(n, dropR[n]);
        }

        /// <summary>A drop gone: merged, wiped or off the glass; its tiles are drawn again without it.</summary>
        private void Remove(int k)
        {
            if (!dropRuns[k])
                MarkDrop(k, Math.Max(dropR[k], dropDrawnR[k]));
            dropR[k] = 0f;
        }

        /// <summary>The tiles a still drop of radius <paramref name="r"/> covers are to be drawn again.</summary>
        private void MarkDrop(int k, float r) => Mark(dropX[k] - r - 2f, dropY[k] - r - 2f, dropX[k] + r + 2f, dropY[k] + r + 2f);

        /// <summary>The tiles over a stretch of the picture (texels) are to be drawn again.</summary>
        private void Mark(float left, float top, float right, float bottom)
        {
            int tx0 = Math.Max(0, (int)Math.Max(0f, left) / Tile), tx1 = Math.Min(TilesAcross - 1, (int)Math.Max(0f, right) / Tile);
            int ty0 = Math.Max(0, (int)Math.Max(0f, top) / Tile), ty1 = Math.Min(TilesDown - 1, (int)Math.Max(0f, bottom) / Tile);
            for (int ty = ty0; ty <= ty1; ty++)
            {
                for (int tx = tx0; tx <= tx1; tx++)
                    tileDirty[ty * TilesAcross + tx] = true;
            }
        }

        /// <summary>The drops gone (dried, merged, wiped, off the glass) taken out of the list.</summary>
        private void Settle()
        {
            int kept = 0;
            for (int k = 0; k < drops; k++)
            {
                if (dropR[k] < 0.35f)
                    continue;
                if (kept != k)
                {
                    dropX[kept] = dropX[k];
                    dropY[kept] = dropY[k];
                    dropR[kept] = dropR[k];
                    dropRuns[kept] = dropRuns[k];
                    dropPhase[kept] = dropPhase[k];
                    dropTravel[kept] = dropTravel[k];
                    dropDrawnR[kept] = dropDrawnR[k];
                    trailX[kept] = trailX[k];
                    trailY[kept] = trailY[k];
                }
                kept++;
            }
            drops = kept;
        }

        /// <summary>One snowflake at random: a small ragged blob.</summary>
        private void Flake()
        {
            float x = Next() * Width, y = Next() * Height;
            Stamp(snow, x, y, 0.8f + 1.6f * Next(), 0.5f + 0.4f * Next(), 1.2f);
            if (Next() < 0.4f)
                Stamp(snow, x + (Next() - 0.5f) * 3f, y + (Next() - 0.5f) * 3f, 0.7f + Next(), 0.4f, 1.2f);
        }

        /// <summary>
        /// The heavy drops run down the glass in fits and starts, wandering a little, gathering the drops they meet, leaving a thin
        /// wet trail and now and then a tiny droplet, and stop as they run thin.
        /// </summary>
        private void Run(float seconds, Vector2 down)
        {
            float pull = down.Length();
            bool downhill = pull >= 0.5f;
            if (downhill)
                downWay = down / pull;
            Vector2 way = downWay, side = new Vector2(-way.Y, way.X);
            int runningNow = 0;
            for (int k = 0; k < drops; k++)
            {
                if (dropRuns[k])
                    runningNow++;
            }
            for (int k = 0; k < drops; k++)
            {
                float r = dropR[k];
                if (r <= 0f)
                    continue;
                if (!dropRuns[k])
                {
                    if (!downhill || r < RunRadius || runningNow >= MostRunning)
                        continue;
                    MarkDrop(k, Math.Max(r, dropDrawnR[k]));
                    dropRuns[k] = true;
                    trailX[k] = dropX[k];
                    trailY[k] = dropY[k];
                    runningNow++;
                }
                if (!downhill)
                {
                    dropRuns[k] = false;
                    dropDrawnR[k] = r;
                    MarkDrop(k, r);
                    continue;
                }
                dropPhase[k] += seconds * (2.5f + 1.5f * Hash(k, 7));
                float phase = dropPhase[k];
                float jerk = 0.25f + 0.75f * Math.Max(0f, (float)Math.Sin(phase)) + 0.2f * (float)Math.Sin(phase * 2.7f);
                float speed = Math.Min(220f, 25f + (r - 2.4f) * 45f) * pull * Math.Max(0.1f, jerk);
                float move = speed * seconds;
                float wander = (float)Math.Sin(phase * 0.6f) * 0.3f * move;
                float fromX = dropX[k], fromY = dropY[k];
                float toX = fromX + way.X * move + side.X * wander, toY = fromY + way.Y * move + side.Y * wander;
                int tx = (int)toX, ty = (int)toY;
                if (tx < 0 || ty < 0 || tx >= PictureWidth || ty >= PictureHeight || pattern.Edge[ty * PictureWidth + tx] < 0f)
                {
                    Remove(k);
                    continue;
                }
                dropX[k] = toX;
                dropY[k] = toY;
                float tdx = toX - trailX[k], tdy = toY - trailY[k];
                if (tdx * tdx + tdy * tdy >= 25f)
                {
                    AddStreak(trailX[k], trailY[k], toX, toY, MathHelper.Clamp(0.22f * r, 0.45f, 1.1f), 9f, smear: false, seed: 0f);
                    trailX[k] = toX;
                    trailY[k] = toY;
                }
                int cell = (ty / Detail) * Width + tx / Detail;
                water[cell] = Math.Min(1f, water[cell] + 0.3f);

                // What it runs into joins it.
                float volume = r * r * r;
                for (int j = 0; j < drops; j++)
                {
                    float rj = dropR[j];
                    if (j == k || rj <= 0f)
                        continue;
                    float dx = dropX[j] - toX, dy = dropY[j] - toY, reachBoth = r + rj * 0.6f;
                    if (dx * dx + dy * dy >= reachBoth * reachBoth)
                        continue;
                    volume += rj * rj * rj;
                    Remove(j);
                }
                // It thins as it wets the glass, and now and then leaves a droplet behind.
                r = (float)Math.Pow(volume, 1.0 / 3.0) - move * 0.006f;
                dropTravel[k] += move;
                if (dropTravel[k] > 5f + 6f * Hash(k, (int)phase))
                {
                    dropTravel[k] = 0f;
                    if (Next() < 0.7f)
                    {
                        float droplet = 0.45f + 0.18f * r * Next();
                        float back = r * 1.1f;
                        r = (float)Math.Pow(Math.Max(0.01, r * r * r - droplet * droplet * droplet), 1.0 / 3.0);
                        dropR[k] = r;
                        AddLoose(toX - way.X * back + side.X * (Next() - 0.5f) * 0.8f, toY - way.Y * back + side.Y * (Next() - 0.5f) * 0.8f, droplet);
                    }
                }
                dropR[k] = r;
                if (r < StopRadius)
                {
                    dropRuns[k] = false;
                    dropDrawnR[k] = r;
                    MarkDrop(k, r);
                    runningNow--;
                }
            }
        }

        /// <summary>A droplet left behind, not merged with the drop that left it.</summary>
        private void AddLoose(float x, float y, float r)
        {
            if (drops >= MostDrops || x < 0f || y < 0f || x >= PictureWidth || y >= PictureHeight || pattern.Edge[(int)y * PictureWidth + (int)x] < 0f)
                return;
            int n = drops++;
            dropX[n] = x;
            dropY[n] = y;
            dropR[n] = dropDrawnR[n] = r;
            dropRuns[n] = false;
            dropPhase[n] = 0f;
            dropTravel[n] = 0f;
            MarkDrop(n, r);
        }

        /// <summary>Adds <paramref name="amount"/> to <paramref name="field"/> round (x, y), softly to <paramref name="radius"/> cells, each cell up to <paramref name="most"/>.</summary>
        private static void Stamp(float[] field, float cx, float cy, float radius, float amount, float most)
        {
            int left = Math.Max(0, (int)(cx - radius)), right = Math.Min(Width - 1, (int)(cx + radius));
            int top = Math.Max(0, (int)(cy - radius)), bottom = Math.Min(Height - 1, (int)(cy + radius));
            float r2 = radius * radius;
            for (int y = top; y <= bottom; y++)
            {
                for (int x = left; x <= right; x++)
                {
                    float dx = x + 0.5f - cx, dy = y + 0.5f - cy, d2 = dx * dx + dy * dy;
                    if (d2 >= r2)
                        continue;
                    int i = y * Width + x;
                    float v = field[i];
                    if (v < most)
                        field[i] = Math.Min(most, v + amount * (float)Math.Sqrt(1f - d2 / r2));
                }
            }
        }

        /// <summary>Premultiplied: (r, g, b) at coverage <paramref name="alpha"/> laid over what is there.</summary>
        private static void Over(ref float r, ref float g, ref float b, ref float a, float cr, float cg, float cb, float alpha)
        {
            if (alpha <= 0f)
                return;
            alpha = Math.Min(1f, alpha);
            float under = 1f - alpha;
            r = cr * alpha + r * under;
            g = cg * alpha + g * under;
            b = cb * alpha + b * under;
            a = alpha + a * under;
        }

        /// <summary>Premultiplied, in the picture's bytes: (r, g, b) at coverage <paramref name="alpha"/> laid over the texel at <paramref name="at"/>.</summary>
        private static void Blend(byte[] texels, int at, float cr, float cg, float cb, float alpha)
        {
            if (alpha <= 0.002f)
                return;
            alpha = Math.Min(1f, alpha);
            float under = (1f - alpha) * (1f / 255f);
            texels[at] = Byte(cr * alpha + texels[at] * under);
            texels[at + 1] = Byte(cg * alpha + texels[at + 1] * under);
            texels[at + 2] = Byte(cb * alpha + texels[at + 2] * under);
            texels[at + 3] = Byte(alpha + texels[at + 3] * under);
        }

        private static byte Byte(float v) => (byte)(v <= 0f ? 0 : v >= 1f ? 255 : (int)(v * 255f + 0.5f));

        private static float Smooth(float from, float to, float x)
        {
            float t = MathHelper.Clamp((x - from) / (to - from), 0f, 1f);
            return t * t * (3f - 2f * t);
        }

        /// <summary>The next of a fixed sequence, 0 to 1 (xorshift): the same glass for the same weather, and nothing allocated.</summary>
        private float Next()
        {
            random ^= random << 13;
            random ^= random >> 17;
            random ^= random << 5;
            return (random >> 8) * (1f / 16777216f);
        }

        /// <summary>Smooth value noise, 0 to 1: a hashed value at each whole point, eased between. Pure.</summary>
        private static float Noise(float x, float y)
        {
            int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y);
            float fx = x - ix, fy = y - iy;
            fx = fx * fx * (3f - 2f * fx);
            fy = fy * fy * (3f - 2f * fy);
            float a = Hash(ix, iy), b = Hash(ix + 1, iy), c = Hash(ix, iy + 1), d = Hash(ix + 1, iy + 1);
            return MathHelper.Lerp(MathHelper.Lerp(a, b, fx), MathHelper.Lerp(c, d, fx), fy);
        }

        private static float Hash(int x, int y)
        {
            unchecked
            {
                uint h = (uint)x * 374761393u + (uint)y * 668265263u;
                h = (h ^ (h >> 13)) * 1274126177u;
                return ((h ^ (h >> 16)) & 0xFFFFFF) / 16777215f;
            }
        }

        /// <summary>
        /// What of the picture is fixed: how much of each texel is glass, how far in from the edge it is, a fine grain, and the
        /// frost's crystals: how much ice each texel holds when frosted over (<see cref="Ice"/>) and how far the cold must reach
        /// for it to frost (<see cref="Arrival"/>, shares of the way in). The crystals grow as frost does on cold glass: feathers
        /// from the rim, each a stem putting out side branches at the 60 deg of ice's hexagons and those their own twigs, over a
        /// granular rime at the very edge and a faint haze between them. Made once; read only after.
        /// </summary>
        private sealed class Pattern
        {
            public readonly float[] Glass = new float[PictureCount], Edge = new float[PictureCount], Grain = new float[PictureCount];
            public readonly float[] Ice = new float[PictureCount], Arrival = new float[PictureCount];

            /// <summary>Per grid cell, the least and most reach its texels' ice waits for (none: least past the most).</summary>
            public readonly float[] CellLow = new float[Count], CellHigh = new float[Count];

            private uint random = 0x2545F491u;
            private float strength = 1f;
            private readonly float centreX, centreY;

            public Pattern()
            {
                for (int y = 0; y < PictureHeight; y++)
                {
                    for (int x = 0; x < PictureWidth; x++)
                    {
                        int i = y * PictureWidth + x;
                        Angles((x + 0.5f) / PictureWidth, (y + 0.5f) / PictureHeight, out float round, out float up);
                        float e = 1f - HelmetShell.OpeningRadius(round, up);
                        Edge[i] = e;
                        Glass[i] = MathHelper.Clamp(e / 0.012f, 0f, 1f);
                        float grain = Noise(x * 0.45f + 31f, y * 0.45f + 7f) * 0.55f + Noise(x * 1.3f, y * 1.3f + 13f) * 0.45f;
                        Grain[i] = grain;
                        // A faint haze between the feathers, behind their tips; a granular rime thick at the very edge.
                        Ice[i] = 0.06f + 0.08f * grain;
                        Arrival[i] = 0.03f + e * 1.3f + 0.05f * Noise(x * 0.04f + 5f, y * 0.04f + 1f);
                        float rimWidth = 0.035f + 0.04f * Noise(x * 0.06f + 17f, y * 0.06f + 3f);
                        float rime = (1f - Smooth(rimWidth * 0.4f, rimWidth, e)) * (0.45f + 0.55f * Noise(x * 0.9f + 2f, y * 0.9f + 8f));
                        if (rime > Ice[i])
                        {
                            Ice[i] = rime;
                            Arrival[i] = Math.Min(Arrival[i], e * 0.6f);
                        }
                    }
                }

                Vector2 middle = Place(0f, 0f);
                centreX = middle.X * PictureWidth;
                centreY = middle.Y * PictureHeight;
                const int Feathers = 280;
                for (int s = 0; s < Feathers; s++)
                {
                    if (Next() < 0.1f)
                        continue;
                    // A point on the opening's edge (the superellipse at this angle), a little outside it, the feather aimed at the middle.
                    double angle = (s + Next() * 0.9f) / Feathers * Math.PI * 2.0;
                    float nx = (float)Math.Cos(angle), ny = (float)Math.Sin(angle);
                    float k = 1.01f / (float)Math.Sqrt(Math.Sqrt(nx * nx * nx * nx + ny * ny * ny * ny));
                    float round = nx * k * HelmetShell.Side, up = ny * k * (ny >= 0f ? HelmetShell.Up : HelmetShell.Down);
                    Vector2 at = Place(round, up);
                    float x = at.X * PictureWidth, y = at.Y * PictureHeight;
                    float dx = centreX - x, dy = centreY - y, toMiddle = (float)Math.Sqrt(dx * dx + dy * dy);
                    if (toMiddle < 1f)
                        continue;
                    dx /= toMiddle;
                    dy /= toMiddle;
                    Turn(ref dx, ref dy, (Next() - 0.5f) * 0.7f);
                    float stretch = 0.45f + 1.1f * Noise((float)angle * 2.2f + 7f, 3f);
                    float lengthShare = Math.Min(0.46f, (0.04f + 0.36f * (float)Math.Pow(Next(), 1.2)) * stretch);
                    strength = 0.55f + 0.45f * Next();
                    Branch(x, y, dx, dy, lengthShare * toMiddle, 1f + 0.6f * Next(), 0f, 1f / toMiddle, 0);
                }

                // Stars: six-armed crystals of their own, the ice's hexagons, scattered over the glass the frost reaches.
                const int Stars = 320;
                for (int n = 0; n < Stars; n++)
                {
                    float x = Next() * PictureWidth, y = Next() * PictureHeight;
                    float e = Edge[(int)y * PictureWidth + (int)x];
                    if (e < 0.02f || e > 0.42f)
                        continue;
                    float size = 2.5f + 6f * Next() * Next(), turn = Next() * MathHelper.TwoPi;
                    strength = 0.5f + 0.4f * Next();
                    for (int arm = 0; arm < 6; arm++)
                    {
                        float dx = (float)Math.Cos(turn + arm * MathHelper.Pi / 3f), dy = (float)Math.Sin(turn + arm * MathHelper.Pi / 3f);
                        Branch(x, y, dx, dy, size * (0.8f + 0.3f * Next()), 0.55f, 0.01f + e * 1.05f, 0.002f, 1);
                    }
                }

                for (int c = 0; c < Count; c++)
                {
                    CellLow[c] = float.MaxValue;
                    CellHigh[c] = float.MinValue;
                }
                for (int y = 0; y < PictureHeight; y++)
                {
                    for (int x = 0; x < PictureWidth; x++)
                    {
                        int i = y * PictureWidth + x, c = (y / Detail) * Width + x / Detail;
                        if (Ice[i] <= 0.005f || Glass[i] <= 0f)
                            continue;
                        CellLow[c] = Math.Min(CellLow[c], Arrival[i]);
                        CellHigh[c] = Math.Max(CellHigh[c], Arrival[i]);
                    }
                }
            }

            /// <summary>
            /// One branch of a feather from (x, y) along (dx, dy), <paramref name="length"/> texels, tapering from
            /// <paramref name="width"/>, frosting as the reach passes <paramref name="arrival"/> plus <paramref name="perTexel"/> for
            /// each texel along it; stems put out branches, and branches twigs, at about 60 deg either side.
            /// </summary>
            private void Branch(float x, float y, float dx, float dy, float length, float width, float arrival, float perTexel, int level)
            {
                const float Step = 0.6f;
                float bend = (Next() - 0.5f) * (level == 0 ? 0.02f : 0.05f);
                float nextBranch = level == 0 ? 2f + 3f * Next() : 0.8f + 1.2f * Next();
                float sideOf = Next() < 0.5f ? 1f : -1f;
                for (float t = 0f; t < length; t += Step)
                {
                    float along = t / length;
                    float w = width * (1f - 0.65f * along);
                    float ice = strength * (level == 0 ? 1f : level == 1 ? 0.85f : 0.7f) * (1f - 0.35f * along);
                    Splat(x, y, w, ice, arrival + t * perTexel);
                    if (level == 0 && ((int)(t / Step) & 1) == 0)
                        Halo(x, y, w * 2f + 3f, ice * 0.24f, arrival + t * perTexel + 0.004f);
                    Turn(ref dx, ref dy, bend * Step + (Next() - 0.5f) * 0.04f);
                    x += dx * Step;
                    y += dy * Step;
                    if (level < 2 && t >= nextBranch)
                    {
                        nextBranch = t + (level == 0 ? 1.5f + 2.2f * Next() : 0.8f + 1.2f * Next());
                        float left = length - t;
                        float sub = Math.Min(level == 0 ? 30f : 7f, left * (level == 0 ? 0.55f : 0.6f) * (0.45f + 0.7f * Next()));
                        if (sub > 1.5f)
                        {
                            float bx = dx, by = dy;
                            Turn(ref bx, ref by, sideOf * MathHelper.ToRadians(45f + 28f * Next()));
                            Branch(x, y, bx, by, sub, w * 0.7f, arrival + t * perTexel, perTexel, level + 1);
                            // Often the other side as well at once, as frost's ferns do.
                            if (Next() < (level == 0 ? 0.55f : 0.7f))
                            {
                                bx = dx;
                                by = dy;
                                Turn(ref bx, ref by, -sideOf * MathHelper.ToRadians(45f + 28f * Next()));
                                Branch(x, y, bx, by, sub * (0.6f + 0.4f * Next()), w * 0.7f, arrival + t * perTexel, perTexel, level + 1);
                            }
                            else
                            {
                                sideOf = -sideOf;
                            }
                        }
                    }
                }
            }

            /// <summary>A round dab of ice, <paramref name="radius"/> texels, edges smoothed over a texel.</summary>
            private void Splat(float cx, float cy, float radius, float ice, float arrival)
            {
                int left = Math.Max(0, (int)(cx - radius - 1f)), right = Math.Min(PictureWidth - 1, (int)(cx + radius + 1f));
                int top = Math.Max(0, (int)(cy - radius - 1f)), bottom = Math.Min(PictureHeight - 1, (int)(cy + radius + 1f));
                for (int y = top; y <= bottom; y++)
                {
                    for (int x = left; x <= right; x++)
                    {
                        float dx = x + 0.5f - cx, dy = y + 0.5f - cy;
                        float cover = MathHelper.Clamp(radius + 0.5f - (float)Math.Sqrt(dx * dx + dy * dy), 0f, 1f);
                        if (cover <= 0f)
                            continue;
                        int i = y * PictureWidth + x;
                        float v = ice * cover * (0.85f + 0.15f * Grain[i]);
                        if (v > Ice[i])
                            Ice[i] = v;
                        if (cover > 0.3f && arrival < Arrival[i])
                            Arrival[i] = arrival;
                    }
                }
            }

            /// <summary>A soft milky body of ice round a branch, <paramref name="radius"/> texels, thinning outward.</summary>
            private void Halo(float cx, float cy, float radius, float ice, float arrival)
            {
                int left = Math.Max(0, (int)(cx - radius)), right = Math.Min(PictureWidth - 1, (int)(cx + radius));
                int top = Math.Max(0, (int)(cy - radius)), bottom = Math.Min(PictureHeight - 1, (int)(cy + radius));
                float inverse = 1f / radius;
                for (int y = top; y <= bottom; y++)
                {
                    for (int x = left; x <= right; x++)
                    {
                        float dx = x + 0.5f - cx, dy = y + 0.5f - cy;
                        float d = (float)Math.Sqrt(dx * dx + dy * dy) * inverse;
                        if (d >= 1f)
                            continue;
                        int i = y * PictureWidth + x;
                        float fall = 1f - d;
                        float v = ice * fall * fall * (0.7f + 0.3f * Grain[i]);
                        if (v > Ice[i])
                        {
                            Ice[i] = v;
                            if (arrival < Arrival[i])
                                Arrival[i] = arrival;
                        }
                    }
                }
            }

            private static void Turn(ref float dx, ref float dy, float radians)
            {
                float c = (float)Math.Cos(radians), s = (float)Math.Sin(radians);
                float x = dx * c - dy * s;
                dy = dx * s + dy * c;
                dx = x;
            }

            private float Next()
            {
                random ^= random << 13;
                random ^= random >> 17;
                random ^= random << 5;
                return (random >> 8) * (1f / 16777216f);
            }
        }
    }
}
