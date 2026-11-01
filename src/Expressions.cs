using System;
using System.Collections.Generic;
using System.Globalization;

namespace SFSVizzyMod
{
    // 极简表达式求值器：数字、变量、四则运算、比较、常用函数。
    public static class Expressions
    {
        public static double Eval(string source, IReadOnlyDictionary<string, double> vars)
        {
            if (string.IsNullOrWhiteSpace(source)) return 0.0;
            return new Parser(source, vars).Parse();
        }

        private sealed class Parser
        {
            private readonly string s;
            private readonly IReadOnlyDictionary<string, double> vars;
            private int i;

            public Parser(string source, IReadOnlyDictionary<string, double> variables)
            {
                s = source;
                vars = variables;
            }

            public double Parse()
            {
                double v = Or();
                Skip();
                if (i < s.Length) throw new Exception("unexpected '" + s[i] + "'");
                return v;
            }

            // ---------- 运算优先级：or < and < not < 比较 < 加减 < 乘除 < 乘方 < 一元负号 < 初等项 ----------

            // or / ||：任一为真就是 1
            private double Or()
            {
                double left = And();
                while (true)
                {
                    int save = i;
                    if (!(TryWord("or") || TryTake("||"))) { i = save; break; }
                    double right = And();
                    left = Bool(left != 0.0 || right != 0.0);
                }
                return left;
            }

            // and / &&：两边都真才是 1
            private double And()
            {
                double left = Not();
                while (true)
                {
                    int save = i;
                    if (!(TryWord("and") || TryTake("&&"))) { i = save; break; }
                    double right = Not();
                    left = Bool(left != 0.0 && right != 0.0);
                }
                return left;
            }

            // not / !：取反
            private double Not()
            {
                int save = i;
                if (TryWord("not") || TryTake("!")) return Bool(Not() == 0.0);
                i = save;
                return Comparison();
            }

            private double Comparison()
            {
                double left = Additive();
                while (true)
                {
                    Skip();
                    if (TryTake("<=")) left = Bool(left <= Additive());
                    else if (TryTake(">=")) left = Bool(left >= Additive());
                    else if (TryTake("==")) left = Bool(Equals_(left, Additive()));
                    else if (TryTake("!=")) left = Bool(!Equals_(left, Additive()));
                    else if (TryTake("<")) left = Bool(left < Additive());
                    else if (TryTake(">")) left = Bool(left > Additive());
                    else break;
                }
                return left;
            }

            private static bool Equals_(double a, double b)
            {
                return Math.Abs(a - b) <= 1e-9 + 1e-9 * Math.Abs(a);
            }

            private double Additive()
            {
                double left = Multiplicative();
                while (true)
                {
                    Skip();
                    if (TryTake("+")) left += Multiplicative();
                    else if (TryTake("-")) left -= Multiplicative();
                    else break;
                }
                return left;
            }

            private double Multiplicative()
            {
                double left = Power();
                while (true)
                {
                    Skip();
                    if (TryTake("*")) left *= Power();
                    else if (TryTake("/"))
                    {
                        double d = Power();
                        left = d == 0.0 ? 0.0 : left / d;   // 除零给 0，别炸程序
                    }
                    else if (TryTake("%"))
                    {
                        double d = Power();
                        left = d == 0.0 ? 0.0 : left % d;
                    }
                    else break;
                }
                return left;
            }

            private double Power()
            {
                double base_ = Unary();
                Skip();
                if (i < s.Length && s[i] == '^')
                {
                    i++;
                    return Math.Pow(base_, Unary());   // 右结合
                }
                return base_;
            }

            private double Unary()
            {
                Skip();
                if (i < s.Length && s[i] == '-')
                {
                    i++;
                    return -Unary();
                }
                if (i < s.Length && s[i] == '+')
                {
                    i++;
                    return Unary();
                }
                return Primary();
            }

            private double Primary()
            {
                Skip();
                if (i >= s.Length) throw new Exception("unexpected end of expression");
                char c = s[i];
                if (c == '(')
                {
                    i++;
                    double v = Or();
                    Skip();
                    if (i >= s.Length || s[i] != ')') throw new Exception("missing ')'");
                    i++;
                    return v;
                }
                if (char.IsDigit(c) || c == '.') return Number();
                if (char.IsLetter(c) || c == '_') return Identifier();
                throw new Exception("unexpected '" + c + "'");
            }

            private double Number()
            {
                int start = i;
                while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
                string text = s.Substring(start, i - start);
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                    throw new Exception("bad number '" + text + "'");
                return v;
            }

            private double Identifier()
            {
                int start = i;
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
                string name = s.Substring(start, i - start);

                // 函数调用
                Skip();
                if (i < s.Length && s[i] == '(') return Call(name);

                if (vars != null && vars.TryGetValue(name, out double v)) return v;
                switch (name.ToLowerInvariant())
                {
                    case "pi": return Math.PI;
                    case "e": return Math.E;
                    case "true": return 1.0;
                    case "false": return 0.0;
                }
                throw new Exception("unknown variable '" + name + "'");
            }

            private double Call(string name)
            {
                i++; // 吃掉 '('
                List<double> args = new List<double>();
                Skip();
                if (i < s.Length && s[i] != ')')
                {
                    args.Add(Or());
                    while (true)
                    {
                        Skip();
                        if (i < s.Length && s[i] == ',')
                        {
                            i++;
                            args.Add(Or());
                        }
                        else break;
                    }
                }
                Skip();
                if (i >= s.Length || s[i] != ')') throw new Exception("missing ')' in call to " + name);
                i++;

                switch (name.ToLowerInvariant())
                {
                    case "min": Require(args, 2, name); return Math.Min(args[0], args[1]);
                    case "max": Require(args, 2, name); return Math.Max(args[0], args[1]);
                    case "abs": Require(args, 1, name); return Math.Abs(args[0]);
                    case "sqrt": Require(args, 1, name); return Math.Sqrt(Math.Max(0.0, args[0]));
                    case "floor": Require(args, 1, name); return Math.Floor(args[0]);
                    case "ceil": Require(args, 1, name); return Math.Ceiling(args[0]);
                    case "round": Require(args, 1, name); return Math.Round(args[0], MidpointRounding.AwayFromZero);
                    case "pow": Require(args, 2, name); return Math.Pow(args[0], args[1]);
                    case "sin": Require(args, 1, name); return Math.Sin(args[0] * Math.PI / 180.0);
                    case "cos": Require(args, 1, name); return Math.Cos(args[0] * Math.PI / 180.0);
                    case "tan": Require(args, 1, name); return Math.Tan(args[0] * Math.PI / 180.0);
                    case "asin": Require(args, 1, name); return Math.Asin(ClampN(args[0], -1.0, 1.0)) * 180.0 / Math.PI;
                    case "acos": Require(args, 1, name); return Math.Acos(ClampN(args[0], -1.0, 1.0)) * 180.0 / Math.PI;
                    case "atan": Require(args, 1, name); return Math.Atan(args[0]) * 180.0 / Math.PI;
                    case "atan2": Require(args, 2, name); return Math.Atan2(args[0], args[1]) * 180.0 / Math.PI;
                    case "log": Require(args, 1, name); return Math.Log10(Math.Max(1e-300, args[0]));
                    case "ln": Require(args, 1, name); return Math.Log(Math.Max(1e-300, args[0]));
                    case "clamp":
                        Require(args, 3, name);
                        return Math.Min(Math.Max(args[0], args[1]), args[2]);
                    default:
                        throw new Exception("unknown function '" + name + "'");
                }
            }

            private static void Require(List<double> args, int n, string name)
            {
                if (args.Count != n)
                    throw new Exception(name + "() expects " + n + " argument(s), got " + args.Count);
            }

            private static double ClampN(double v, double lo, double hi)
            {
                return v < lo ? lo : (v > hi ? hi : v);
            }

            // ---------- 基础 ----------

            private void Skip()
            {
                while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            }

            // 匹配关键字（and / or / not），后面不能紧跟字母或数字，免得把 order 当 or
            private bool TryWord(string word)
            {
                Skip();
                int n = word.Length;
                if (i + n > s.Length) return false;
                if (string.CompareOrdinal(s, i, word, 0, n) != 0) return false;
                if (i + n < s.Length && (char.IsLetterOrDigit(s[i + n]) || s[i + n] == '_')) return false;
                i += n;
                return true;
            }

            private bool TryTake(string op)
            {
                Skip();
                if (i + op.Length <= s.Length && string.CompareOrdinal(s, i, op, 0, op.Length) == 0)
                {
                    i += op.Length;
                    return true;
                }
                return false;
            }

            private static double Bool(bool b) => b ? 1.0 : 0.0;
        }
    }
}
