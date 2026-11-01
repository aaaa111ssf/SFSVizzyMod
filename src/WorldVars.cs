using System;
using System.Collections.Generic;
using SFS.Parts.Modules;
using SFS.World;
using SFS.World.Maps;

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

            // 轨道根数：地表静止时 TryCreateOrbit 会失败，跳过即可
            try
            {
                bool ok;
                Orbit orbit = Orbit.TryCreateOrbit(loc, false, false, out ok);
                if (ok && orbit != null)
                {
                    double pe = orbit.periapsis - loc.planet.Radius;
                    double ap = orbit.apoapsis - loc.planet.Radius;
                    vars["Periapsis"] = double.IsFinite(pe) ? pe : 0.0;
                    vars["Apoapsis"] = double.IsFinite(ap) ? ap : 1e12;
                    vars["Eccentricity"] = double.IsFinite(orbit.ecc) ? orbit.ecc : 0.0;
                    vars["Escaping"] = orbit.ecc >= 1.0 || orbit.apoapsis >= loc.planet.SOI ? 1.0 : 0.0;
                }
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
