using System;
using System.Collections.Generic;
using SFS.Parts.Modules;
using SFS.World;
using SFS.World.Maps;
using SFS.WorldBase;

namespace SFSVizzyMod
{
    // 从玩家火箭提取运行时变量（与游戏内口径一致）。
    public static class WorldVars
    {
        public static Rocket GetPlayerRocket()
        {
            if (PlayerController.main == null || (object)PlayerController.main.player == null) return null;
            return PlayerController.main.player.Value as Rocket;
        }

        // 变量表：高度/速度用米，油门 0..1，角度用度，TWR 无量纲。
        public static void Fill(Rocket r, Dictionary<string, double> vars)
        {
            if (r == null) return;
            Location loc = r.location.Value;
            double mass = r.mass.GetMass();

            // 推力统计：只算已激活(engineOn)的发动机 —— 千万别把二级也点着
            double active = 0.0, available = 0.0;
            EngineModule[] engines = r.partHolder.GetModules<EngineModule>();
            for (int i = 0; i < engines.Length; i++)
            {
                EngineModule e = engines[i];
                if (e.engineOn.Value)
                {
                    double t = e.thrust.Value;
                    available += t;
                    active += t * e.throttle_Out.Value;
                }
            }

            // 速度分解：与 Location.VerticalVelocity 相同的旋转取 y / x
            double relAngle = loc.position.AngleRadians - Math.PI / 2.0;
            double vertical = loc.velocity.Rotate(-relAngle).y;
            double horizontal = loc.velocity.Rotate(-relAngle).x;

            vars["Throttle"] = r.throttle.output_Throttle.Value;
            vars["Altitude"] = loc.Height;
            // 与游戏 HUD 同一口径：clampToWater=true，落水时以水面为地面；（LocationDrawer 用的也是 GetTerrainHeight(clampToWater: true)）
            vars["AltitudeAGL"] = loc.GetTerrainHeight(true);
            vars["Speed"] = loc.velocity.magnitude;
            vars["VerticalSpeed"] = vertical;
            vars["HorizontalSpeed"] = horizontal;
            vars["Mass"] = mass;
            vars["TWR"] = mass > 0.0 ? active / mass : 0.0;
            vars["MaxTWR"] = mass > 0.0 ? available / mass : 0.0;

            // 姿态：rb2d.rotation 是世界角，减去当地天顶的世界角得到相对姿态；（重发射 / 绕到行星另一侧时读数连续，不会跳）
            try
            {
                vars["Heading"] = NormalizeAngle(r.rb2d.rotation - UpWorldAngle(loc));
                vars["AngularVel"] = r.rb2d.angularVelocity;
            }
            catch (Exception) { }

            // 环境：重力 / 大气密度 / 动压 / 大气层顶
            try
            {
                vars["Gravity"] = loc.planet.GetGravity(loc.Radius);
            }
            catch (Exception) { }
            try
            {
                double density = loc.planet.GetAtmosphericDensity(loc.Height);
                vars["AirDensity"] = double.IsFinite(density) ? density : 0.0;
                vars["DynamicPressure"] = vars.TryGetValue("AirDensity", out double rho)
                    ? 0.5 * rho * loc.velocity.magnitude * loc.velocity.magnitude
                    : 0.0;
            }
            catch (Exception) { }
            try
            {
                vars["AtmosphereTop"] = loc.planet.HasAtmospherePhysics ? loc.planet.AtmosphereHeightPhysics : 0.0;
            }
            catch (Exception) { }

            // 先给默认值：发射台上 TryCreateOrbit 会失败，变量留空会让程序报
            // unknown variable 直接中止。没有轨道时远拱点按当前高度算，垂直上升也说得通
            vars["Apoapsis"] = loc.Height;
            vars["Periapsis"] = 0.0;
            vars["Eccentricity"] = 0.0;
            vars["Escaping"] = 0.0;
            // 到拱点给个大数而不是 0：0 会让 "wait until TimeToApoapsis < 30" 立刻触发
            vars["TimeToApoapsis"] = 1e9;
            vars["TimeToPeriapsis"] = 1e9;
            vars["DistToApoapsis"] = 0.0;
            vars["DistToPeriapsis"] = 0.0;
            // 落点：没有交点就给 1e9 / 0，免得 wait until TimeToImpact < 10 立刻触发
            vars["TimeToImpact"] = 1e9;
            vars["ImpactDistance"] = 0.0;
            vars["ImpactLongitude"] = 0.0;
            vars["ImpactToTarget"] = 0.0;

            // 轨道根数：地表静止时 TryCreateOrbit 会失败，跳过即可
            // 第 2 个参数要 true，否则近拱点时刻和平均角速度算不出来
            Orbit orbit = null;
            try
            {
                bool ok;
                orbit = Orbit.TryCreateOrbit(loc, true, false, out ok);
                if (ok && orbit != null)
                {
                    double pe = orbit.periapsis - loc.planet.Radius;
                    double ap = orbit.apoapsis - loc.planet.Radius;
                    vars["Periapsis"] = double.IsFinite(pe) ? pe : 0.0;
                    vars["Apoapsis"] = double.IsFinite(ap) ? ap : 1e12;
                    vars["Eccentricity"] = double.IsFinite(orbit.ecc) ? orbit.ecc : 0.0;
                    vars["Escaping"] = orbit.ecc >= 1.0 || orbit.apoapsis >= loc.planet.SOI ? 1.0 : 0.0;

                    FillApsisTimes(orbit, loc, vars);
                }
            }
            catch (Exception) { }

            // 落点：轨道段解析、进大气后带阻力积分，实现在 Trajectory.cs
            FillImpact(r, orbit, loc, vars);

            // 世界状态：加速档位和游戏时间，写等待条件时有用
            vars["TimewarpIndex"] = WorldTime.main != null ? WorldTime.main.timewarpIndex : 0.0;
            vars["WorldTime"] = WorldTime.main != null ? WorldTime.main.worldTime : 0.0;
            try
            {
                vars["RocketCount"] = GameManager.main != null && GameManager.main.rockets != null
                    ? GameManager.main.rockets.Count : 0.0;
            }
            catch (Exception) { }

            // 燃料：按资源类型名汇总（原版叫 "Fuel"）
            try
            {
                double amount = 0.0, capacity = 0.0;
                ResourceModule[] resources = r.partHolder.GetModules<ResourceModule>();
                for (int i = 0; i < resources.Length; i++)
                {
                    ResourceModule res = resources[i];
                    if (res.resourceType == null) continue;
                    string typeName = res.resourceType.name;
                    if (string.IsNullOrEmpty(typeName) || typeName.IndexOf("fuel", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    capacity += res.TotalResourceCapacity;
                    amount += res.ResourceAmount;
                }
                vars["FuelAmount"] = amount;
                vars["FuelCapacity"] = capacity;
                vars["Fuel"] = capacity > 0.0 ? Math.Min(1.0, Math.Max(0.0, amount / capacity)) : 0.0;
            }
            catch (Exception) { }

            vars["Radius"] = loc.Radius;
            vars["PlanetRadius"] = loc.planet != null ? loc.planet.Radius : 0.0;

            // 目标导航：距离/方位/经度，未选目标时为 0
            try
            {
                SelectableObject target = GetTarget();
                if (target != null)
                {
                    Location tl = target.Location;
                    Double2 delta;
                    if (tl.planet == loc.planet) delta = tl.position - loc.position;
                    else
                    {
                        double t = WorldTime.main != null ? WorldTime.main.worldTime : 0.0;
                        delta = tl.GetSolarSystemPosition(t) - loc.GetSolarSystemPosition(t);
                    }
                    vars["TargetDistance"] = delta.magnitude;
                    vars["TargetBearing"] = NormalizeAngle(Math.Atan2(delta.x, delta.y) * 180.0 / Math.PI - ZenithAngle(loc));
                    vars["TargetLongitude"] = NormalizeAngle(tl.position.AngleRadians * 180.0 / Math.PI);
                    vars["HasTarget"] = 1.0;
                }
            }
            catch (Exception) { }
        }

        // 落点：算不出来就保持 1e9 / 0，让 wait until 条件不会误触发
        private static void FillImpact(Rocket r, Orbit orbit, Location loc, Dictionary<string, double> vars)
        {
            try
            {
                Trajectory.Result hit;
                if (!Trajectory.Predict(r, orbit, out hit)) return;

                vars["TimeToImpact"] = hit.Seconds;
                vars["ImpactDistance"] = hit.Distance;
                vars["ImpactLongitude"] = NormalizeAngle(hit.AngleDeg);

                // 选着目标时顺手给出落点到目标多远，定点降落要用
                SelectableObject target = GetTarget();
                if (target == null) return;
                Location tl = target.Location;
                if (tl.planet == loc.planet) vars["ImpactToTarget"] = (hit.Position - tl.position).magnitude;
            }
            catch (Exception) { }
        }

        // 到远/近拱点的时间与直线距离：轨道时间参数没算出来就给大数/0
        private static void FillApsisTimes(Orbit orbit, Location loc, Dictionary<string, double> vars)
        {
            double now = WorldTime.main != null ? WorldTime.main.worldTime : 0.0;

            double tAp = double.NaN, tPe = double.NaN;
            try { tAp = orbit.GetNextTrueAnomalyPassTime(now, Math.PI); } catch (Exception) { }
            try { tPe = orbit.GetNextTrueAnomalyPassTime(now, 0.0); } catch (Exception) { }

            double dAp = TimeOrFallback(tAp - now);
            double dPe = TimeOrFallback(tPe - now);
            vars["TimeToApoapsis"] = dAp;
            vars["TimeToPeriapsis"] = dPe;

            try
            {
                if (dAp < 1e8) vars["DistToApoapsis"] = (orbit.GetLocation(tAp).position - loc.position).magnitude;
            }
            catch (Exception) { }
            try
            {
                if (dPe < 1e8) vars["DistToPeriapsis"] = (orbit.GetLocation(tPe).position - loc.position).magnitude;
            }
            catch (Exception) { }
        }

        // 逃离轨道没有下一个拱点：算不出来或算出负数就返回一个大数
        private static double TimeOrFallback(double seconds)
        {
            if (!double.IsFinite(seconds) || seconds < 0.0) return 1e9;
            return seconds > 1e8 ? 1e9 : seconds;
        }

        // 设置 / 清除地图目标。mode = nearest / planet / rocket / clear
        public static bool SetTarget(string mode, int index, Rocket self, out string info)
        {
            info = "";
            try
            {
                if (Map.navigation == null) { info = "map not ready"; return false; }

                if (mode == "clear")
                {
                    Map.navigation.SetTarget(null);
                    info = "cleared";
                    return true;
                }

                if (mode == "planet")
                {
                    Planet here = self.location.Value.planet;
                    for (int i = 0; i < SelectableObject.mapObjects.Count; i++)
                    {
                        MapPlanet mp = SelectableObject.mapObjects[i] as MapPlanet;
                        if (mp != null && mp.planet == here)
                        {
                            Map.navigation.SetTarget(mp);
                            info = mp.planet.DisplayName;
                            return true;
                        }
                    }
                    info = "planet icon not found";
                    return false;
                }

                // nearest / rocket N：都从「别的飞船」里挑，按距离排序
                List<MapRocket> list = new List<MapRocket>();
                for (int i = 0; i < SelectableObject.mapObjects.Count; i++)
                {
                    MapRocket mr = SelectableObject.mapObjects[i] as MapRocket;
                    if (mr == null || mr.rocket == null || mr.rocket == self) continue;
                    list.Add(mr);
                }
                if (list.Count == 0) { info = "no other rockets"; return false; }

                Location here2 = self.location.Value;
                list.Sort(delegate (MapRocket x, MapRocket y)
                {
                    double dx = (x.rocket.location.Value.position - here2.position).magnitude;
                    double dy = (y.rocket.location.Value.position - here2.position).magnitude;
                    return dx.CompareTo(dy);
                });

                int pick = mode == "nearest" ? 1 : index;
                if (pick < 1 || pick > list.Count)
                {
                    info = "index out of range (1.." + list.Count + ")";
                    return false;
                }

                Map.navigation.SetTarget(list[pick - 1]);
                info = "#" + pick + " of " + list.Count;
                return true;
            }
            catch (Exception e)
            {
                info = e.Message;
                return false;
            }
        }

        public static SelectableObject GetTarget()
        {
            try { return Map.navigation != null ? Map.navigation.target : null; }
            catch (Exception) { return null; }
        }

        public static double TargetWorldAngle(Location loc)
        {
            SelectableObject target = GetTarget();
            if (target == null) return double.NaN;
            Location tl = target.Location;
            Double2 delta;
            if (tl.planet == loc.planet) delta = tl.position - loc.position;
            else
            {
                double t = WorldTime.main != null ? WorldTime.main.worldTime : 0.0;
                delta = tl.GetSolarSystemPosition(t) - loc.GetSolarSystemPosition(t);
            }
            return Math.Atan2(delta.x, delta.y) * 180.0 / Math.PI;
        }

        // 归一化到 (-180, 180]。
        public static double NormalizeAngle(double deg)
        {
            double v = deg % 360.0;
            if (v > 180.0) v -= 360.0;
            else if (v <= -180.0) v += 360.0;
            return v;
        }

        // "机头朝当地天顶" 时的 rb2d.rotation 世界角（度）。
        public static double UpWorldAngle(Location loc)
        {
            return loc.position.AngleRadians * 180.0 / Math.PI - 90.0;
        }

        // 当地天顶方向（= 推力朝上、垂直上升姿态）的世界角度（度）。与 Rocket.GetRotation() 同一口径：正上方 = 90°，因此它是本模组所有；turn / turn_to 角度的基准。
        public static double ZenithAngle(Location loc)
        {
            return loc.position.AngleRadians * 180.0 / Math.PI;
        }

        // 速度方向的世界角度（度），用于 prograde / retrograde。
        public static double VelocityAngle(Location loc)
        {
            return Math.Atan2(loc.velocity.y, loc.velocity.x) * 180.0 / Math.PI;
        }

        // "机头顺速度方向"(prograde) 时的 rb2d.rotation 世界角（度）。
        public static double ProgradeWorldAngle(Location loc)
        {
            return Math.Atan2(loc.velocity.y, loc.velocity.x) * 180.0 / Math.PI - 90.0;
        }
    }
}
