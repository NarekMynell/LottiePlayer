using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Mynell.Lottie.Expressions
{
    public enum JsType : byte { Undefined, Null, Boolean, Number, String, Object }

    /// <summary>A JavaScript value (primitive or object reference).</summary>
    public readonly struct JsValue
    {
        public readonly JsType Type;
        public readonly double Num;
        public readonly object Ref;

        JsValue(JsType type, double num, object r)
        {
            Type = type;
            Num = num;
            Ref = r;
        }

        public static readonly JsValue Undefined = default;
        public static readonly JsValue Null = new JsValue(JsType.Null, 0, null);
        public static readonly JsValue True = new JsValue(JsType.Boolean, 1, null);
        public static readonly JsValue False = new JsValue(JsType.Boolean, 0, null);

        public static JsValue Number(double d) => new JsValue(JsType.Number, d, null);
        public static JsValue Bool(bool b) => b ? True : False;
        public static JsValue String(string s) => new JsValue(JsType.String, 0, s ?? "");
        public static JsValue Object(JsObject o) => o == null ? Null : new JsValue(JsType.Object, 0, o);
        public static implicit operator JsValue(double d) => Number(d);
        public static implicit operator JsValue(JsObject o) => Object(o);

        public bool IsUndefined => Type == JsType.Undefined;
        public bool IsNullish => Type == JsType.Undefined || Type == JsType.Null;
        public bool IsNumber => Type == JsType.Number;
        public bool IsString => Type == JsType.String;
        public bool IsObject => Type == JsType.Object;
        public JsObject Obj => Ref as JsObject;
        public string Str => Ref as string;
        public JsArray Arr => Ref as JsArray;

        public static string NumberToString(double d)
        {
            if (double.IsNaN(d)) return "NaN";
            if (double.IsPositiveInfinity(d)) return "Infinity";
            if (double.IsNegativeInfinity(d)) return "-Infinity";
            if (d == Math.Floor(d) && Math.Abs(d) < 1e21) return ((long)d).ToString(CultureInfo.InvariantCulture);
            return d.ToString("R", CultureInfo.InvariantCulture);
        }

        public override string ToString() => JsOps.ToStr(this);
    }

    /// <summary>Base JavaScript object (property bag). Host objects override the virtual members.</summary>
    public class JsObject
    {
        Dictionary<string, JsValue> _props;

        public virtual string ClassName => "Object";
        public virtual bool IsCallable => false;

        public virtual JsValue Get(string key)
        {
            if (_props != null && _props.TryGetValue(key, out var v)) return v;
            return JsValue.Undefined;
        }

        public virtual void Set(string key, JsValue value)
        {
            _props ??= new Dictionary<string, JsValue>();
            _props[key] = value;
        }

        public virtual bool Has(string key) => _props != null && _props.ContainsKey(key);

        public virtual IEnumerable<string> Keys() => _props != null ? (IEnumerable<string>)_props.Keys : Array.Empty<string>();

        public virtual JsValue Call(JsInterpreter it, JsValue thisValue, JsValue[] args) =>
            throw new JsRuntimeError($"{ClassName} is not a function");

        /// <summary>Host values (e.g. property references) convert to a primitive / array value here.</summary>
        public virtual bool TryToPrimitive(JsInterpreter it, out JsValue value)
        {
            value = default;
            return false;
        }
    }

    public sealed class JsArray : JsObject
    {
        public readonly List<JsValue> Items;

        public JsArray(int capacity = 4) { Items = new List<JsValue>(capacity); }
        public JsArray(List<JsValue> items) { Items = items; }

        public static JsArray FromDoubles(double[] v)
        {
            var a = new JsArray(v.Length);
            foreach (var d in v) a.Items.Add(d);
            return a;
        }

        public static JsArray FromFloats(float[] v, int offset, int count)
        {
            var a = new JsArray(count);
            for (int i = 0; i < count; i++) a.Items.Add(v[offset + i]);
            return a;
        }

        public override string ClassName => "Array";
        public int Length => Items.Count;

        public override JsValue Get(string key)
        {
            if (key == "length") return Items.Count;
            if (int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out int i)) return i < Items.Count ? Items[i] : JsValue.Undefined;
            return base.Get(key);
        }

        public override void Set(string key, JsValue value)
        {
            if (key == "length")
            {
                int n = (int)JsOps.ToNumber(null, value);
                while (Items.Count > n) Items.RemoveAt(Items.Count - 1);
                while (Items.Count < n) Items.Add(JsValue.Undefined);
                return;
            }
            if (int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out int i)) SetIndex(i, value);
            else base.Set(key, value);
        }

        public void SetIndex(int i, JsValue value)
        {
            if (i < 0 || i > 1 << 20) return;
            while (Items.Count <= i) Items.Add(JsValue.Undefined);
            Items[i] = value;
        }

        public override bool Has(string key) =>
            key == "length" || (int.TryParse(key, out int i) && i >= 0 && i < Items.Count) || base.Has(key);

        public override IEnumerable<string> Keys()
        {
            for (int i = 0; i < Items.Count; i++) yield return i.ToString(CultureInfo.InvariantCulture);
        }

        public JsArray Clone() => new JsArray(new List<JsValue>(Items));
    }

    public delegate JsValue NativeFn(JsInterpreter it, JsValue thisValue, JsValue[] args);

    public sealed class JsNativeFunction : JsObject
    {
        public readonly string Name;
        readonly NativeFn _fn;

        public JsNativeFunction(string name, NativeFn fn)
        {
            Name = name;
            _fn = fn;
        }

        public override string ClassName => "Function";
        public override bool IsCallable => true;
        public override JsValue Call(JsInterpreter it, JsValue thisValue, JsValue[] args) => _fn(it, thisValue, args);
    }

    sealed class JsFunction : JsObject
    {
        public FuncExpr Node;
        public Scope Closure;
        public JsValue LexicalThis;
        public override string ClassName => "Function";
        public override bool IsCallable => true;
        public override JsValue Call(JsInterpreter it, JsValue thisValue, JsValue[] args) => it.Invoke(this, thisValue, args);
    }

    public sealed class JsRuntimeError : Exception
    {
        public JsRuntimeError(string message) : base(message) { }
    }

    sealed class JsThrow : Exception
    {
        public readonly JsValue Value;
        public JsThrow(JsValue v) : base("Uncaught " + JsOps.ToStr(v)) { Value = v; }
    }

    sealed class Scope
    {
        public readonly Dictionary<string, JsValue> Vars = new Dictionary<string, JsValue>();
        public readonly Scope Parent;
        public Scope(Scope parent) { Parent = parent; }
    }

    /// <summary>
    /// Tree-walking interpreter for expression code. Globals are resolved through <see cref="GlobalResolver"/>
    /// after the local scopes. A step budget protects against runaway loops.
    /// </summary>
    public sealed class JsInterpreter
    {
        /// <summary>Host lookup for identifiers not found in any scope (return false when unknown).</summary>
        public delegate bool GlobalLookup(string name, out JsValue value);

        public GlobalLookup GlobalResolver;
        public int StepLimit = 2_000_000;
        /// <summary>Deterministic generator behind Math.random / random().</summary>
        internal readonly JsBuiltins.Rng Random = new JsBuiltins.Rng();
        /// <summary>Value of the last top-level expression statement of the last <see cref="Run"/>.</summary>
        public JsValue LastExpressionValue { get; private set; }
        int _depth;
        int _steps;
        JsValue _return;
        readonly Scope _global = new Scope(null);

        enum Flow : byte { Normal, Return, Break, Continue }

        public JsInterpreter()
        {
            JsBuiltins.Install(this);
        }

        public void DefineGlobal(string name, JsValue value) => _global.Vars[name] = value;

        public void DefineFunction(string name, NativeFn fn) => _global.Vars[name] = new JsNativeFunction(name, fn);

        /// <summary>Runs a parsed program in a fresh scope and returns that scope's variables.</summary>
        internal Dictionary<string, JsValue> Run(List<Stmt> program)
        {
            if (_depth == 0) _steps = 0;
            _depth++;
            try
            {
                var scope = new Scope(_global);
                Hoist(program, scope);
                JsValue last = JsValue.Undefined;
                foreach (var s in program)
                {
                    if (s is ExprStmt es)
                    {
                        Step();
                        last = Eval(es.E, scope);
                        continue;
                    }
                    var f = Exec(s, scope);
                    if (f == Flow.Return) break;
                }
                LastExpressionValue = last;
                return scope.Vars;
            }
            finally
            {
                _depth--;
            }
        }

        void Step()
        {
            if (++_steps > StepLimit) throw new JsRuntimeError("Expression exceeded its time budget");
        }

        // ------------------------------------------------------------------ hoisting

        static void Hoist(List<Stmt> body, Scope scope)
        {
            foreach (var s in body) HoistStmt(s, scope);
        }

        static void HoistStmt(Stmt s, Scope scope)
        {
            switch (s)
            {
                case VarDecl v:
                    foreach (var d in v.Decls)
                        if (!scope.Vars.ContainsKey(d.Key)) scope.Vars[d.Key] = JsValue.Undefined;
                    break;
                case FuncDecl f:
                    scope.Vars[f.Func.Name] = new JsFunction { Node = f.Func, Closure = scope };
                    break;
                case IfStmt i:
                    HoistStmt(i.Then, scope);
                    if (i.Else != null) HoistStmt(i.Else, scope);
                    break;
                case ForStmt fs:
                    if (fs.Init != null) HoistStmt(fs.Init, scope);
                    HoistStmt(fs.Body, scope);
                    break;
                case ForInStmt fi:
                    if (!scope.Vars.ContainsKey(fi.Var)) scope.Vars[fi.Var] = JsValue.Undefined;
                    HoistStmt(fi.Body, scope);
                    break;
                case WhileStmt w: HoistStmt(w.Body, scope); break;
                case DoWhileStmt dw: HoistStmt(dw.Body, scope); break;
                case BlockStmt b: foreach (var x in b.Body) HoistStmt(x, scope); break;
                case SwitchStmt sw: foreach (var c in sw.Cases) foreach (var x in c.Value) HoistStmt(x, scope); break;
                case TryStmt t:
                    HoistStmt(t.Block, scope);
                    if (t.Handler != null) HoistStmt(t.Handler, scope);
                    if (t.Finalizer != null) HoistStmt(t.Finalizer, scope);
                    break;
            }
        }

        // ------------------------------------------------------------------ statements

        Flow Exec(Stmt s, Scope sc)
        {
            Step();
            switch (s)
            {
                case ExprStmt e:
                    Eval(e.E, sc);
                    return Flow.Normal;
                case VarDecl v:
                    foreach (var d in v.Decls)
                    {
                        if (d.Value != null) Assign(sc, d.Key, Eval(d.Value, sc), true);
                        else if (!Declared(sc, d.Key)) sc.Vars[d.Key] = JsValue.Undefined;
                    }
                    return Flow.Normal;
                case IfStmt i:
                    if (JsOps.ToBool(this, Eval(i.Test, sc))) return Exec(i.Then, sc);
                    return i.Else != null ? Exec(i.Else, sc) : Flow.Normal;
                case BlockStmt b:
                    foreach (var x in b.Body)
                    {
                        var f = Exec(x, sc);
                        if (f != Flow.Normal) return f;
                    }
                    return Flow.Normal;
                case ForStmt fs:
                {
                    if (fs.Init != null) Exec(fs.Init, sc);
                    while (fs.Test == null || JsOps.ToBool(this, Eval(fs.Test, sc)))
                    {
                        var f = Exec(fs.Body, sc);
                        if (f == Flow.Break) break;
                        if (f == Flow.Return) return f;
                        if (fs.Update != null) Eval(fs.Update, sc);
                        Step();
                    }
                    return Flow.Normal;
                }
                case ForInStmt fi:
                {
                    var obj = Eval(fi.Obj, sc);
                    var list = new List<JsValue>();
                    if (obj.Obj is JsArray arr)
                    {
                        for (int k = 0; k < arr.Items.Count; k++) list.Add(fi.Of ? arr.Items[k] : JsValue.String(k.ToString(CultureInfo.InvariantCulture)));
                    }
                    else if (obj.Obj != null)
                    {
                        foreach (var key in obj.Obj.Keys()) list.Add(fi.Of ? obj.Obj.Get(key) : JsValue.String(key));
                    }
                    else if (obj.IsString)
                    {
                        string str = obj.Str;
                        for (int k = 0; k < str.Length; k++) list.Add(fi.Of ? JsValue.String(str[k].ToString()) : JsValue.String(k.ToString(CultureInfo.InvariantCulture)));
                    }
                    foreach (var item in list)
                    {
                        Assign(sc, fi.Var, item, false);
                        var f = Exec(fi.Body, sc);
                        if (f == Flow.Break) break;
                        if (f == Flow.Return) return f;
                    }
                    return Flow.Normal;
                }
                case WhileStmt w:
                    while (JsOps.ToBool(this, Eval(w.Test, sc)))
                    {
                        var f = Exec(w.Body, sc);
                        if (f == Flow.Break) break;
                        if (f == Flow.Return) return f;
                        Step();
                    }
                    return Flow.Normal;
                case DoWhileStmt dw:
                    do
                    {
                        var f = Exec(dw.Body, sc);
                        if (f == Flow.Break) break;
                        if (f == Flow.Return) return f;
                        Step();
                    } while (JsOps.ToBool(this, Eval(dw.Test, sc)));
                    return Flow.Normal;
                case ReturnStmt r:
                    _return = r.Value != null ? Eval(r.Value, sc) : JsValue.Undefined;
                    return Flow.Return;
                case BreakStmt _: return Flow.Break;
                case ContinueStmt _: return Flow.Continue;
                case FuncDecl _: return Flow.Normal; // hoisted
                case EmptyStmt _: return Flow.Normal;
                case SwitchStmt sw:
                {
                    var d = Eval(sw.Disc, sc);
                    int start = -1;
                    for (int k = 0; k < sw.Cases.Count && start < 0; k++)
                        if (sw.Cases[k].Key != null && JsOps.StrictEquals(this, d, Eval(sw.Cases[k].Key, sc))) start = k;
                    if (start < 0)
                        for (int k = 0; k < sw.Cases.Count; k++)
                            if (sw.Cases[k].Key == null) { start = k; break; }
                    if (start < 0) return Flow.Normal;
                    for (int k = start; k < sw.Cases.Count; k++)
                        foreach (var x in sw.Cases[k].Value)
                        {
                            var f = Exec(x, sc);
                            if (f == Flow.Break) return Flow.Normal;
                            if (f != Flow.Normal) return f;
                        }
                    return Flow.Normal;
                }
                case TryStmt t:
                {
                    Flow result = Flow.Normal;
                    try
                    {
                        result = Exec(t.Block, sc);
                    }
                    catch (Exception ex) when (t.Handler != null && (ex is JsThrow || ex is JsRuntimeError))
                    {
                        if (t.Param != null)
                            sc.Vars[t.Param] = ex is JsThrow jt ? jt.Value : JsValue.String(ex.Message);
                        result = Exec(t.Handler, sc);
                    }
                    finally
                    {
                        if (t.Finalizer != null) Exec(t.Finalizer, sc);
                    }
                    return result;
                }
                case ThrowStmt th:
                    throw new JsThrow(Eval(th.Value, sc));
            }
            throw new JsRuntimeError("Unsupported statement");
        }

        static bool Declared(Scope sc, string name)
        {
            for (var s = sc; s != null; s = s.Parent) if (s.Vars.ContainsKey(name)) return true;
            return false;
        }

        void Assign(Scope sc, string name, JsValue value, bool declareLocal)
        {
            for (var s = sc; s != null; s = s.Parent)
            {
                if (s.Vars.ContainsKey(name))
                {
                    s.Vars[name] = value;
                    return;
                }
            }
            // Undeclared: create in the nearest function scope (sloppy mode), or here when declaring.
            sc.Vars[name] = value;
        }

        JsValue Lookup(Scope sc, string name, int pos)
        {
            for (var s = sc; s != null; s = s.Parent)
                if (s.Vars.TryGetValue(name, out var v)) return v;
            if (GlobalResolver != null && GlobalResolver(name, out var g)) return g;
            throw new JsRuntimeError($"{name} is not defined");
        }

        // ------------------------------------------------------------------ expressions

        internal JsValue Eval(Expr e, Scope sc)
        {
            Step();
            switch (e)
            {
                case NumLit n: return n.Value;
                case StrLit s: return JsValue.String(s.Value);
                case BoolLit b: return JsValue.Bool(b.Value);
                case NullLit _: return JsValue.Null;
                case UndefinedLit _: return JsValue.Undefined;
                case Ident id: return Lookup(sc, id.Name, id.Pos);
                case ThisExpr _: return Lookup(sc, "this", e.Pos);
                case ArrayLit a:
                {
                    var arr = new JsArray(a.Items.Count);
                    foreach (var item in a.Items) arr.Items.Add(Eval(item, sc));
                    return arr;
                }
                case ObjectLit o:
                {
                    var obj = new JsObject();
                    foreach (var p in o.Props) obj.Set(p.Key, Eval(p.Value, sc));
                    return obj;
                }
                case MemberExpr m: return GetMember(Eval(m.Obj, sc), m.Name);
                case IndexExpr ix:
                {
                    var obj = Eval(ix.Obj, sc);
                    var key = Eval(ix.Index, sc);
                    return GetIndexed(obj, key);
                }
                case CallExpr c: return EvalCall(c, sc);
                case NewExpr ne:
                {
                    var callee = Eval(ne.Callee, sc);
                    var args = EvalArgs(ne.Args, sc);
                    if (callee.Obj == null || !callee.Obj.IsCallable) throw new JsRuntimeError("Not a constructor");
                    var self = new JsObject();
                    var r = callee.Obj.Call(this, self, args);
                    return r.IsObject ? r : self;
                }
                case UnaryExpr u: return EvalUnary(u, sc);
                case UpdateExpr up:
                {
                    var old = JsOps.ToNumber(this, Eval(up.Target, sc));
                    double nv = up.Op == "++" ? old + 1 : old - 1;
                    Store(up.Target, nv, sc);
                    return up.Prefix ? nv : old;
                }
                case BinaryExpr b: return JsOps.Binary(this, b.Op, Eval(b.L, sc), Eval(b.R, sc));
                case LogicalExpr l:
                {
                    var left = Eval(l.L, sc);
                    switch (l.Op)
                    {
                        case "&&": return JsOps.ToBool(this, left) ? Eval(l.R, sc) : left;
                        case "||": return JsOps.ToBool(this, left) ? left : Eval(l.R, sc);
                        default: return left.IsNullish ? Eval(l.R, sc) : left;
                    }
                }
                case AssignExpr asg:
                {
                    JsValue v;
                    if (asg.Op == "=") v = Eval(asg.Value, sc);
                    else
                    {
                        var cur = Eval(asg.Target, sc);
                        v = JsOps.Binary(this, asg.Op.Substring(0, asg.Op.Length - 1), cur, Eval(asg.Value, sc));
                    }
                    Store(asg.Target, v, sc);
                    return v;
                }
                case CondExpr ce: return JsOps.ToBool(this, Eval(ce.Test, sc)) ? Eval(ce.Then, sc) : Eval(ce.Else, sc);
                case SeqExpr seq:
                {
                    JsValue last = JsValue.Undefined;
                    foreach (var x in seq.Items) last = Eval(x, sc);
                    return last;
                }
                case FuncExpr f:
                {
                    var fn = new JsFunction { Node = f, Closure = sc };
                    if (f.Arrow) fn.LexicalThis = TryLookup(sc, "this");
                    return fn;
                }
            }
            throw new JsRuntimeError("Unsupported expression");
        }

        JsValue TryLookup(Scope sc, string name)
        {
            for (var s = sc; s != null; s = s.Parent)
                if (s.Vars.TryGetValue(name, out var v)) return v;
            return JsValue.Undefined;
        }

        JsValue EvalUnary(UnaryExpr u, Scope sc)
        {
            if (u.Op == "typeof")
            {
                if (u.Operand is Ident id)
                {
                    for (var s = sc; s != null; s = s.Parent)
                        if (s.Vars.TryGetValue(id.Name, out var v)) return JsValue.String(JsOps.TypeOf(v));
                    if (GlobalResolver != null && GlobalResolver(id.Name, out var g)) return JsValue.String(JsOps.TypeOf(g));
                    return JsValue.String("undefined");
                }
                return JsValue.String(JsOps.TypeOf(Eval(u.Operand, sc)));
            }
            if (u.Op == "delete")
            {
                return JsValue.True;
            }
            var x = Eval(u.Operand, sc);
            switch (u.Op)
            {
                case "!": return JsValue.Bool(!JsOps.ToBool(this, x));
                case "-": return JsOps.Negate(this, x);
                case "+": return JsOps.ToNumber(this, x);
                case "~": return ~JsOps.ToInt32(JsOps.ToNumber(this, x));
                case "void": return JsValue.Undefined;
            }
            throw new JsRuntimeError("Unsupported unary operator " + u.Op);
        }

        void Store(Expr target, JsValue v, Scope sc)
        {
            switch (target)
            {
                case Ident id:
                    Assign(sc, id.Name, v, false);
                    return;
                case MemberExpr m:
                {
                    var obj = Eval(m.Obj, sc);
                    if (obj.Obj != null) obj.Obj.Set(m.Name, v);
                    return;
                }
                case IndexExpr ix:
                {
                    var obj = Eval(ix.Obj, sc);
                    var key = Eval(ix.Index, sc);
                    if (obj.Obj is JsArray arr && key.IsNumber) arr.SetIndex((int)key.Num, v);
                    else if (obj.Obj != null) obj.Obj.Set(JsOps.ToKey(key), v);
                    return;
                }
            }
            throw new JsRuntimeError("Invalid assignment target");
        }

        JsValue[] EvalArgs(List<Expr> args, Scope sc)
        {
            if (args.Count == 0) return Array.Empty<JsValue>();
            var r = new JsValue[args.Count];
            for (int i = 0; i < r.Length; i++) r[i] = Eval(args[i], sc);
            return r;
        }

        JsValue EvalCall(CallExpr c, Scope sc)
        {
            JsValue thisValue = JsValue.Undefined;
            JsValue callee;
            if (c.Callee is MemberExpr m)
            {
                thisValue = Eval(m.Obj, sc);
                callee = GetMember(thisValue, m.Name);
            }
            else if (c.Callee is IndexExpr ix)
            {
                thisValue = Eval(ix.Obj, sc);
                callee = GetIndexed(thisValue, Eval(ix.Index, sc));
            }
            else callee = Eval(c.Callee, sc);
            var args = EvalArgs(c.Args, sc);
            var fn = callee.Obj;
            if (fn == null || !fn.IsCallable)
            {
                string name = c.Callee is Ident id ? id.Name : c.Callee is MemberExpr mm ? mm.Name : "expression";
                throw new JsRuntimeError($"{name} is not a function");
            }
            return fn.Call(this, thisValue, args);
        }

        internal JsValue Invoke(JsFunction f, JsValue thisValue, JsValue[] args)
        {
            var scope = new Scope(f.Closure);
            var node = f.Node;
            for (int i = 0; i < node.Params.Count; i++)
                scope.Vars[node.Params[i]] = i < args.Length ? args[i] : JsValue.Undefined;
            if (!node.Arrow)
            {
                scope.Vars["this"] = thisValue;
                var argArr = new JsArray(args.Length);
                argArr.Items.AddRange(args);
                scope.Vars["arguments"] = argArr;
            }
            if (node.ExprBody != null) return Eval(node.ExprBody, scope);
            Hoist(node.Body, scope);
            foreach (var s in node.Body)
            {
                var flow = Exec(s, scope);
                if (flow == Flow.Return)
                {
                    var r = _return;
                    _return = JsValue.Undefined;
                    return r;
                }
            }
            return JsValue.Undefined;
        }

        /// <summary>Calls a JS or native function value.</summary>
        public JsValue CallFunction(JsValue fn, JsValue thisValue, params JsValue[] args)
        {
            if (fn.Obj == null || !fn.Obj.IsCallable) throw new JsRuntimeError("Not a function");
            return fn.Obj.Call(this, thisValue, args);
        }

        // ------------------------------------------------------------------ members

        public JsValue GetMember(JsValue obj, string name)
        {
            switch (obj.Type)
            {
                case JsType.Object:
                {
                    var o = obj.Obj;
                    var v = o.Get(name);
                    if (!v.IsUndefined || o.Has(name)) return v;
                    if (o is JsArray) return JsBuiltins.ArrayMethod(name);
                    if (o.IsCallable && (name == "call" || name == "apply" || name == "bind")) return JsBuiltins.FunctionMethod(name);
                    // Host values behave like their primitive (e.g. property.value.length).
                    if (o.TryToPrimitive(this, out var prim) && !(prim.Obj == o)) return GetMember(prim, name);
                    return JsValue.Undefined;
                }
                case JsType.String:
                    if (name == "length") return obj.Str.Length;
                    return JsBuiltins.StringMethod(name);
                case JsType.Number:
                    return JsBuiltins.NumberMethod(name);
                case JsType.Undefined:
                case JsType.Null:
                    throw new JsRuntimeError($"Cannot read property '{name}' of {(obj.Type == JsType.Null ? "null" : "undefined")}");
            }
            return JsValue.Undefined;
        }

        public JsValue GetIndexed(JsValue obj, JsValue key)
        {
            if (obj.Obj is JsArray arr && key.IsNumber)
            {
                int i = (int)key.Num;
                return i >= 0 && i < arr.Items.Count && i == key.Num ? arr.Items[i] : JsValue.Undefined;
            }
            if (obj.IsString && key.IsNumber)
            {
                int i = (int)key.Num;
                return i >= 0 && i < obj.Str.Length ? JsValue.String(obj.Str[i].ToString()) : JsValue.Undefined;
            }
            return GetMember(obj, JsOps.ToKey(key));
        }
    }
}
