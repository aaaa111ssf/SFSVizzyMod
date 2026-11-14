using System;
using System.Collections.Generic;
using SFS.World;
using SFS.World.Drag;
using SFS.WorldBase;
using UnityEngine;

namespace SFSVizzyMod
{
    // 落点预测：轨道段用开普勒直接算到大气顶，进大气后按游戏自己的阻力公式一步步积分
    public static class Trajectory
    {
        // 一次预测的结果；Valid = false 表示这趟落不到地面
        public struct Result
        {
            public bool Valid;
            public double Seconds;      // 还有多久触地
            public double AngleDeg;     // 落点的世界角，也就是经度
            public double Distance;     // 现在位置到落点的直线距离
            public Double2 Position;    // 落点位置（行星局部坐标）
        }

        // 缓存：一次下降要跑几千步，同一枚火箭 20 帧内直接复用上次结果
        private static Rocket cacheRocket;
        private static int cacheFrame = -1000;
        private static Result cacheResult;
        private static bool warnedNoArea;   // 只报一次，别刷满日志

        private const double StepHigh = 0.2;     // 高空步子放大，省时间
        private const double StepLow = 0.05;     // 进低空换小步子，落点才准
        private const double MaxSeconds = 900.0; // 最长只往后看 15 分钟

        public static bool Predict(Rocket r, Orbit orbit, out Result res)
        {
            if (r != null && cacheRocket == r && Time.frameCount - cacheFrame < 20)
            {
                res = cacheResult;
                return res.Valid;
            }

            cacheRocket = r;
            cacheFrame = Time.frameCount;
            try
            {
                res = Compute(r, orbit);
            }
            catch (Exception)
            {
                res = default(Result);
            }
            cacheResult = res;
            return res.Valid;
        }

        private static Result Compute(Rocket r, Orbit orbit)
        {
            if (r == null) return default(Result);
            Location loc = r.location.Value;
            if (loc.planet == null) return default(Result);

            // 停在发射台上的时候预测落点没意义，直接说不知道
            if (loc.Height < 5.0 && loc.velocity.magnitude < 1.0) return default(Result);

            Planet planet = loc.planet;
            double surfR = planet.Radius;
            bool hasAtmo = planet.HasAtmospherePhysics && planet.AtmosphereHeightPhysics > 1.0;
            double atmoR = hasAtmo ? surfR + planet.AtmosphereHeightPhysics : surfR;

            Location start = loc;
            double offset = 0.0;

            // 还在真空段：先用轨道解析走到大气顶（没有大气就直接走到地面）
            if (loc.Radius > atmoR)
            {
                if (orbit == null || orbit.periapsis > atmoR) return default(Result);
                double nu = Kepler.GetTrueAnomalyAtRadius(atmoR, orbit.slr, orbit.ecc);
                if (double.IsNaN(nu)) return default(Result);

                double now = WorldTime.main != null ? WorldTime.main.worldTime : 0.0;
                // 取下降穿过大气顶的那一次，方向要跟着轨道顺逆
                double cross = orbit.GetNextTrueAnomalyPassTime(now, nu * -orbit.direction);
                if (!double.IsFinite(cross) || cross < now) return default(Result);

                offset = cross - now;
                if (offset > MaxSeconds) return default(Result);
                start = orbit.GetLocation(cross);
            }

            // 没有大气就不必积分了，解析给的那个点已经在地面上
            if (!hasAtmo) return Fill(start.position, loc, offset);

            // 还在往上爬就先不预测了，不然悬停 / 上升段会白跑满时间上限
            if (start.Radius < atmoR + 1000.0 && Double2.Dot(start.velocity, start.position) > 0.0)
                return default(Result);

            // 有大气：从大气顶开始按重力 + 阻力积分
            double k = DragK(r, loc);
            Double2 pos = start.position;
            Double2 vel = start.velocity;
            double t = 0.0;

            while (t < MaxSeconds)
            {
                double dt = (pos.magnitude - surfR) > 20000.0 ? StepHigh : StepLow;
                Double2 acc = planet.GetGravity(pos) + DragAccel(planet, pos, vel, k);
                Double2 nvel = vel + acc * dt;
                Double2 npos = pos + nvel * dt;
                double nr = npos.magnitude;

                if (nr <= surfR)
                {
                    // 落在两步之间，按比例插一下，否则落点会差出好几公里
                    double denom = pos.magnitude - nr;
                    double f = denom > 1e-6 ? (pos.magnitude - surfR) / denom : 1.0;
                    if (f < 0.0) f = 0.0;
                    if (f > 1.0) f = 1.0;
                    Double2 hit = pos + (npos - pos) * f;
                    return Fill(hit, loc, offset + t + dt * f);
                }

                // 又冲出大气了，说明这趟是掠过，不会落地
                if (nr > atmoR && Double2.Dot(nvel, npos) > 0.0) return default(Result);

                pos = npos;
                vel = nvel;
                t += dt;
            }
            return default(Result);
        }

        private static Result Fill(Double2 hit, Location loc, double seconds)
        {
            Result res = new Result();
            res.Valid = true;
            res.Seconds = seconds;
            res.AngleDeg = Math.Atan2(hit.y, hit.x) * 180.0 / Math.PI;
            res.Distance = (hit - loc.position).magnitude;
            res.Position = hit;
            return res;
        }

        // 阻力加速度：和游戏一样是 1.5 * 迎风面积 * v² * 密度，方向永远逆着速度
        private static Double2 DragAccel(Planet planet, Double2 pos, Double2 vel, double k)
        {
            if (!(k > 0.0)) return Double2.zero;
            double rho = planet.GetAtmosphericDensity(pos.magnitude - planet.Radius);
            if (!(rho > 0.0)) return Double2.zero;
            double v = vel.magnitude;
            if (v < 1e-6) return Double2.zero;
            return vel.normalized * (-k * v * v * rho);
        }

        // 迎风面积 / 质量。面积算法照抄 AeroModule.CalculateDragForce（它本身是私有的），
        // 但读的都是公开字段，所以不用反射
        private static double DragK(Rocket r, Location loc)
        {
            try
            {
                double mass = r.mass.GetMass();
                if (!(mass > 0.0)) return 0.0;

                // 游戏默认假设机身顺着速度方向飞，这里保持一致
                float num = (float)loc.velocity.AngleRadians - Mathf.PI / 2f;
                List<Surface> surfaces = AeroModule.GetExposedSurfaces(
                    Aero_Rocket.GetDragSurfaces(r.partHolder, Matrix2x2.Angle(0f - num)));

                double area = 0.0;
                for (int i = 0; i < surfaces.Count; i++)
                {
                    Vector2 v = surfaces[i].line.end - surfaces[i].line.start;
                    if (v.x < 0.01f) continue;
                    double w = v.x / (v.x + Math.Abs(v.y));
                    area += v.x * w;
                }
                return 1.5 * area / mass;
            }
            catch (Exception e)
            {
                // 拿不到阻力面就退化成真空，至少变量还在；日志只打一次
                if (!warnedNoArea)
                {
                    warnedNoArea = true;
                    Debug.LogWarning("[SFSVizzy] drag area unavailable, falling back to vacuum: " + e);
                }
                return 0.0;
            }
        }
    }
}
