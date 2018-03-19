module Fable.Transforms.FableTransforms

open Fable
open Fable.AST.Fable
open Microsoft.FSharp.Compiler.SourceCodeServices

// TODO: Use trampoline here?
let rec visit f e =
    match e with
    | IdentExpr _ | Import _ | Debugger _ -> e
    | Value kind ->
        match kind with
        | This _ | Null _ | UnitConstant
        | BoolConstant _ | CharConstant _ | StringConstant _
        | NumberConstant _ | RegexConstant _ | Enum _ -> e
        | NewOption(e, t) -> NewOption(Option.map (visit f) e, t) |> Value
        | NewTuple exprs -> NewTuple(List.map (visit f) exprs) |> Value
        | NewArray(kind, t) ->
            match kind with
            | ArrayValues exprs -> NewArray(ArrayValues(List.map (visit f) exprs), t) |> Value
            | ArrayAlloc _ -> e
        | NewList(ht, t) ->
            let ht = ht |> Option.map (fun (h,t) -> visit f h, visit f t)
            NewList(ht, t) |> Value
        | NewRecord(exprs, ent, genArgs) ->
            NewRecord(List.map (visit f) exprs, ent, genArgs) |> Value
        | NewErasedUnion(e, genArgs) ->
            NewErasedUnion(visit f e, genArgs) |> Value
        | NewUnion(exprs, uci, ent, genArgs) ->
            NewUnion(List.map (visit f) exprs, uci, ent, genArgs) |> Value
    | Test(e, kind, r) -> Test(visit f e, kind, r)
    | Cast(e, t) -> Cast(visit f e, t)
    | Function(kind, body, name) -> Function(kind, visit f body, name)
    | ObjectExpr(members, t, baseCall) ->
        let baseCall = Option.map (visit f) baseCall
        let members = members |> List.map (fun (n,v,k) -> n, visit f v, k)
        ObjectExpr(members, t, baseCall)
    | Operation(kind, t, r) ->
        match kind with
        | CurriedApply(callee, args) ->
            Operation(CurriedApply(visit f callee, List.map (visit f) args), t, r)
        | Call(kind, info) ->
            let kind =
                match kind with
                | ConstructorCall e -> ConstructorCall(visit f e)
                | StaticCall e -> StaticCall(visit f e)
                | InstanceCall memb -> InstanceCall(Option.map (visit f) memb)
            let info = { info with ThisArg = Option.map (visit f) info.ThisArg
                                   Args = List.map (visit f) info.Args }
            Operation(Call(kind, info), t, r)
        | Emit(macro, info) ->
            let info = info |> Option.map (fun info ->
                { info with ThisArg = Option.map (visit f) info.ThisArg
                            Args = List.map (visit f) info.Args })
            Operation(Emit(macro, info), t, r)
        | UnaryOperation(operator, operand) ->
            Operation(UnaryOperation(operator, visit f operand), t, r)
        | BinaryOperation(op, left, right) ->
            Operation(BinaryOperation(op, visit f left, visit f right), t, r)
        | LogicalOperation(op, left, right) ->
            Operation(LogicalOperation(op, visit f left, visit f right), t, r)
    | Get(e, kind, t, r) ->
        match kind with
        | ListHead | ListTail | OptionValue | TupleGet _ | UnionTag _
        | UnionField _ | RecordGet _ -> Get(visit f e, kind, t, r)
        | ExprGet e2 -> Get(visit f e, ExprGet (visit f e2), t, r)
    | Throw(e, typ, r) -> Throw(visit f e, typ, r)
    | Sequential exprs -> Sequential(List.map (visit f) exprs)
    | Let(bs, body) ->
        let bs = bs |> List.map (fun (i,e) -> i, visit f e)
        Let(bs, visit f body)
    | IfThenElse(cond, thenExpr, elseExpr) ->
        IfThenElse(visit f cond, visit f thenExpr, visit f elseExpr)
    | Set(e, kind, v, r) ->
        match kind with
        | VarSet | RecordSet _ ->
            Set(visit f e, kind, visit f v, r)
        | ExprSet e2 -> Set(visit f e, ExprSet (visit f e2), visit f v, r)
    | Loop (kind, r) ->
        match kind with
        | While(e1, e2) -> Loop(While(visit f e1, visit f e2), r)
        | For(i, e1, e2, e3, up) -> Loop(For(i, visit f e1, visit f e2, visit f e3, up), r)
        | ForOf(i, e1, e2) -> Loop(ForOf(i, visit f e1, visit f e2), r)
    | TryCatch(body, catch, finalizer) ->
        TryCatch(visit f body,
                 Option.map (fun (i, e) -> i, visit f e) catch,
                 Option.map (visit f) finalizer)
    | DecisionTree(expr, targets) ->
        let targets = targets |> List.map (fun (idents, v) -> idents, visit f v)
        DecisionTree(visit f expr, targets)
    | DecisionTreeSuccess(idx, boundValues, t) ->
        DecisionTreeSuccess(idx, List.map (visit f) boundValues, t)
    |> f

let getSubExpressions = function
    | IdentExpr _ | Import _ | Debugger _ -> []
    | Value kind ->
        match kind with
        | This _ | Null _ | UnitConstant
        | BoolConstant _ | CharConstant _ | StringConstant _
        | NumberConstant _ | RegexConstant _ | Enum _ -> []
        | NewOption(e, _) -> Option.toList e
        | NewTuple exprs -> exprs
        | NewArray(kind, _) ->
            match kind with ArrayValues exprs -> exprs | ArrayAlloc _ -> []
        | NewList(ht, _) ->
            match ht with Some(h,t) -> [h;t] | None -> []
        | NewRecord(exprs, _, _) -> exprs
        | NewErasedUnion(e, _) -> [e]
        | NewUnion(exprs, _, _, _) -> exprs
    | Test(e, _, _) -> [e]
    | Cast(e, _) -> [e]
    | Function(_, body, _) -> [body]
    | ObjectExpr(members, _, baseCall) ->
        let members = members |> List.map (fun (_,v,_) -> v)
        match baseCall with Some b -> b::members | None -> members
    | Operation(kind, _, _) ->
        match kind with
        | CurriedApply(callee, args) -> callee::args
        | Call(kind, info) ->
            let e1 =
                match kind with
                | ConstructorCall e -> [e]
                | StaticCall e -> [e]
                | InstanceCall memb -> Option.toList memb
            e1 @ (Option.toList info.ThisArg) @ info.Args
        | Emit(_, info) ->
            match info with Some info -> (Option.toList info.ThisArg) @ info.Args | None -> []
        | UnaryOperation(_, operand) -> [operand]
        | BinaryOperation(_, left, right) -> [left; right]
        | LogicalOperation(_, left, right) -> [left; right]
    | Get(e, kind, _, _) ->
        match kind with
        | ListHead | ListTail | OptionValue | TupleGet _ | UnionTag _
        | UnionField _ | RecordGet _ -> [e]
        | ExprGet e2 -> [e; e2]
    | Throw(e, _, _) -> [e]
    | Sequential exprs -> exprs
    | Let(bs, body) -> (List.map snd bs) @ [body]
    | IfThenElse(cond, thenExpr, elseExpr) -> [cond; thenExpr; elseExpr]
    | Set(e, kind, v, _) ->
        match kind with
        | VarSet | RecordSet _ -> [e; v]
        | ExprSet e2 -> [e; e2; v]
    | Loop (kind, _) ->
        match kind with
        | While(e1, e2) -> [e1; e2]
        | For(_, e1, e2, e3, _) -> [e1; e2; e3]
        | ForOf(_, e1, e2) -> [e1; e2]
    | TryCatch(body, catch, finalizer) ->
        match catch with
        | Some(_,c) -> body::c::(Option.toList finalizer)
        | None -> body::(Option.toList finalizer)
    | DecisionTree(expr, targets) -> expr::(List.map snd targets)
    | DecisionTreeSuccess(_, boundValues, _) -> boundValues

let replaceValues replacements expr =
    if Map.isEmpty replacements
    then expr
    else expr |> visit (function
        | IdentExpr id as e ->
            match Map.tryFind id.Name replacements with
            | Some e -> e
            | None -> e
        | e -> e)

let isReferencedMoreThan limit identName e =
    match e with
    // Don't try to erase bindings to these expressions
    | Import _ | Throw _ | Sequential _ | IfThenElse _
    | DecisionTree _ | TryCatch _ -> true
    | _ ->
        let mutable count = 0
        // TODO: Can we optimize this to shortcircuit when count > limit?
        e |> visit (function
            | _ when count > limit -> e
            | IdentExpr id2 as e when id2.Name = identName ->
                count <- count + 1; e
            // // TODO: Decision tree target branchs can be duplicated, so for now
            // // don't remove the binding until we optimize decision trees
            // | DecisionTree _ ->
            //     count <- limit + 1; e
            | e -> e) |> ignore
        count > limit

module private Transforms =
    let (|EntFullName|_|) fullName (ent: FSharpEntity) =
        match ent.TryFullName with
        | Some fullName2 when fullName = fullName2 -> Some EntFullName
        | _ -> None

    let (|ListLiteral|_|) e =
        let rec untail t acc = function
            | Value(NewList(None, _)) -> Some(List.rev acc, t)
            | Value(NewList(Some(head, tail), _)) -> untail t (head::acc) tail
            | _ -> None
        match e with
        | Value(NewList(None, t)) -> Some([], t)
        | Value(NewList(Some(head, tail), t)) -> untail t [head] tail
        | _ -> None

    let (|LambdaOrDelegate|_|) = function
        | Function(Lambda arg, body, name) -> Some([arg], body, name)
        | Function(Delegate args, body, name) -> Some(args, body, name)
        | _ -> None

    let rec (|NestedLambda|_|) expr =
        let rec nestedLambda accArgs body name =
            match body with
            | Function(Lambda arg, body, None) ->
                nestedLambda (arg::accArgs) body name
            | _ -> Some(NestedLambda(List.rev accArgs, body, name))
        match expr with
        | Function(Lambda arg, body, name) -> nestedLambda [arg] body name
        | _ -> None

    // TODO!!!: Some cases of coertion shouldn't be erased
    // string :> seq #1279
    // list (and others) :> seq in Fable 2.0
    // concrete type :> interface in Fable 2.0
    let resolveCasts_required (com: ICompiler) = function
        | Cast(e, t) ->
            match t with
            | DeclaredType(EntFullName Types.enumerable, _) ->
                match e with
                | ListLiteral(exprs, t) -> NewArray(ArrayValues exprs, t) |> Value
                | e -> Replacements.toSeq t e
            | DeclaredType(ent, _) when ent.IsInterface ->
                FSharp2Fable.Util.castToInterface com t ent e
            | _ -> e
        | e -> e

    let lambdaBetaReduction (_: ICompiler) e =
        let applyArgs (args: Ident list) argExprs body =
            let bindings, replacements =
                (([], Map.empty), args, argExprs)
                |||> List.fold2 (fun (bindings, replacements) ident expr ->
                    if hasDoubleEvalRisk expr && isReferencedMoreThan 1 ident.Name body
                    then (ident, expr)::bindings, replacements
                    else bindings, Map.add ident.Name expr replacements)
            match bindings with
            | [] -> replaceValues replacements body
            | bindings -> Let(List.rev bindings, replaceValues replacements body)
        match e with
        // TODO: `Invoke` calls for delegates? Partial applications too?
        // TODO: Don't inline if one of the arguments is `this`?
        | Operation(CurriedApply(NestedLambda(args, body, None), argExprs), _, _)
            when List.sameLength args argExprs ->
            applyArgs args argExprs body
        | e -> e

    // TODO: Other erasable getters? (List head...)
    let getterBetaReduction (_: ICompiler) = function
        | Get(Value(NewTuple exprs), TupleGet index, _, _) -> List.item index exprs
        | Get(Value(NewOption(Some expr, _)), OptionValue, _, _) -> expr
        | e -> e

    let bindingBetaReduction (_: ICompiler) e =
        match e with
        // Don't try to optimize bindings with multiple ident-value pairs
        // as they can reference each other
        | Let([ident, value], body) when not ident.IsMutable ->
            let identName = ident.Name
            if hasDoubleEvalRisk value |> not
                || isReferencedMoreThan 1 identName body |> not
            then
                let value =
                    match value with
                    // TODO: Check if current name is Some? Shouldn't happen...
                    | Function(args, body, _) -> Function(args, body, Some identName)
                    | value -> value
                replaceValues (Map [identName, value]) body
            else e
        | e -> e

    let rec uncurryLambdaType acc = function
        | FunctionType(LambdaType argType, returnType) ->
            uncurryLambdaType (argType::acc) returnType
        | returnType -> List.rev acc, returnType

    let getUncurriedArity (e: Expr) =
        match e.Type with
        | FunctionType(DelegateType argTypes, _) -> List.length argTypes |> Some
        // TODO!!!: Consider record fields as uncurried
        | _ -> None

    let uncurryExpr arity expr =
        let rec (|UncurriedLambda|_|) (arity, expr) =
            let rec uncurryLambda name accArgs remainingArity expr =
                if remainingArity = Some 0
                then Function(Delegate(List.rev accArgs), expr, name) |> Some
                else
                    match expr, remainingArity with
                    | Function(Lambda arg, body, name2), _ ->
                        let remainingArity = remainingArity |> Option.map (fun x -> x - 1)
                        uncurryLambda (Option.orElse name2 name) (arg::accArgs) remainingArity body
                    // If there's no arity expectation we can return the flattened part
                    | _, None when List.isEmpty accArgs |> not ->
                        Function(Delegate(List.rev accArgs), expr, name) |> Some
                    // We cannot flatten lambda to the expected arity
                    | _, _ -> None
            uncurryLambda None [] arity expr
        match arity, expr with
        // | Some (0|1), expr -> expr // This shouldn't happen
        | UncurriedLambda lambda -> lambda
        | Some arity, _ ->
            match getUncurriedArity expr with
            | Some arity2 when arity = arity2 -> expr
            | _ ->
                let argTypes, returnType = uncurryLambdaType [] expr.Type
                let t = FunctionType(DelegateType argTypes, returnType)
                Replacements.uncurryExpr t arity expr
        | None, _ -> expr

    let uncurryBodies idents body1 body2 =
        let replaceIdentType replacements (id: Ident) =
            match Map.tryFind id.Name replacements with
            | Some typ -> { id with Type = typ }
            | None -> id
        let replaceBody uncurried body =
            visit (function
                | IdentExpr id -> replaceIdentType uncurried id |> IdentExpr
                | e -> e) body
        let uncurried =
            (Map.empty, idents) ||> List.fold (fun uncurried id ->
                match uncurryLambdaType [] id.Type with
                | argTypes, returnType when List.isMultiple argTypes ->
                    Map.add id.Name (FunctionType(DelegateType argTypes, returnType)) uncurried
                | _ -> uncurried)
        if Map.isEmpty uncurried
        then idents, body1, body2
        else
            let idents = idents |> List.map (replaceIdentType uncurried)
            let body1 = replaceBody uncurried body1
            let body2 = Option.map (replaceBody uncurried) body2
            idents, body1, body2

    let rec lambdaMayEscapeScope identName = function
        | Operation(CurriedApply(IdentExpr ident,_),_,_) when ident.Name = identName -> false
        | IdentExpr ident when ident.Name = identName -> true
        | e -> getSubExpressions e |> List.exists (lambdaMayEscapeScope identName)

    let uncurryInnerFunctions (_: ICompiler) = function
        | Let([ident, NestedLambda(args, fnBody, _)], letBody) as e when List.isMultiple args ->
            // We need to check the function doesn't leave the current context
            if lambdaMayEscapeScope ident.Name letBody |> not then
                let idents, fnBody, letBody = uncurryBodies [ident] fnBody (Some letBody)
                Let([List.head idents, Function(Delegate args, fnBody, None)], letBody.Value)
            else e
        | Operation(CurriedApply((NestedLambda(args, fnBody, Some name) as lambda), argExprs), t, r)
                        when List.isMultiple args && List.sameLength args argExprs ->
            let ident = makeTypedIdent lambda.Type name
            let idents, fnBody, _ = uncurryBodies [ident] fnBody None
            let info = argInfo None argExprs (args |> List.map (fun a -> a.Type) |> Some)
            Function(Delegate args, fnBody, Some (List.head idents).Name)
            |> staticCall r t info
        | e -> e

    let uncurryReceivedArgs_required (_: ICompiler) e =
        match e with
        | Function(Lambda arg, body, name) ->
            let args, body, _ = uncurryBodies [arg] body None
            Function(Lambda (List.head args), body, name)
        | Function(Delegate args, body, name) ->
            let args, body, _ = uncurryBodies args body None
            Function(Delegate args, body, name)
        | e -> e

    let uncurrySendingArgs_required (_: ICompiler) (e: Expr) =
        let mapArgs f argTypes args =
            let rec mapArgsInner f acc argTypes args =
                match argTypes, args with
                | head1::tail1, head2::tail2 ->
                    let x = f head1 head2
                    mapArgsInner f (x::acc) tail1 tail2
                | [], args2 -> (List.rev acc)@args2
                | _, [] -> List.rev acc
            mapArgsInner f [] argTypes args
        let uncurryArgs argTypes args =
            match argTypes with
            | Some [] -> args // Do nothing
            | Some argTypes ->
                (argTypes, args) ||> mapArgs (fun argType arg ->
                    match uncurryLambdaType [] argType with
                    | argTypes, _ when List.isMultiple argTypes ->
                        let arity = List.length argTypes |> Some
                        uncurryExpr arity arg
                    | _ -> arg)
            | None -> List.map (uncurryExpr None) args
        match e with
        // TODO!!!: Uncurry also NewRecord and CurriedApply arguments
        | Operation(Call(kind, info), t, r) ->
            // For CurriedApply: let argTypes = uncurryLambdaType [] t |> fst |> Some
            let info = { info with Args = uncurryArgs info.ArgTypes info.Args }
            Operation(Call(kind, info), t, r)
        | Operation(Emit(macro, Some info), t, r) ->
            let info = { info with Args = uncurryArgs info.ArgTypes info.Args }
            Operation(Emit(macro, Some info), t, r)
        | e -> e

    let uncurryApplications_required (_: ICompiler) e =
        match e with
        // TODO: Check for nested applications?
        | Operation(CurriedApply(applied, args), t, r) as e when List.isMultiple args ->
            match applied.Type with
            | FunctionType(DelegateType argTypes, _) ->
                if List.sameLength argTypes args then
                    let info = argInfo None args (Some argTypes)
                    staticCall r t info applied
                else
                    Replacements.partialApply t (argTypes.Length - args.Length) applied args
            | _ -> e
        | e -> e

    let unwrapFunctions (_: ICompiler) e =
        let sameArgs args1 args2 =
            List.forall2 (fun (a1: Ident) -> function
                | IdentExpr a2 -> a1.Name = a2.Name
                | _ -> false) args1 args2
        match e with
        | LambdaOrDelegate(args, Operation(Call(StaticCall funcExpr, info), _, _), _)
            when Option.isNone info.ThisArg && sameArgs args info.Args -> funcExpr
        | e -> e

open Transforms

// ATTENTION: Order of transforms matters for optimizations
let optimizeExpr (com: ICompiler) e =
    // TODO: Optimize decision trees
    // TODO: Optimize binary operations with numerical or string literals
    // TODO: Optimize gets if the expression is a value instead of ident
    // Example: `List.item 1 [1; 2]` can become `1`
    [ // First apply beta reduction
      bindingBetaReduction
      lambdaBetaReduction
      getterBetaReduction
      // Then resolve casts
      resolveCasts_required
      // Then apply uncurry optimizations
      // Required as fable-core and bindings expect it
      uncurryReceivedArgs_required
      uncurrySendingArgs_required
      uncurryInnerFunctions
      uncurryApplications_required
      unwrapFunctions
    ] |> List.fold (fun e f -> visit (f com) e) e

let rec optimizeDeclaration (com: ICompiler) = function
    | ActionDeclaration expr ->
        ActionDeclaration(optimizeExpr com expr)
    | ValueDeclaration(value, info) ->
        ValueDeclaration(optimizeExpr com value, info)
    | ImplicitConstructorDeclaration(args, body, info) ->
        ImplicitConstructorDeclaration(args, optimizeExpr com body, info)
    | OverrideDeclaration(args, body, info) ->
        OverrideDeclaration(args, optimizeExpr com body, info)
    | InterfaceCastDeclaration(members, info) ->
        let members = members |> List.map (fun (n,v,k) -> n, optimizeExpr com v, k)
        InterfaceCastDeclaration(members, info)

let optimizeFile (com: ICompiler) (file: File) =
    let newDecls = List.map (optimizeDeclaration com) file.Declarations
    File(file.SourcePath, newDecls, usedVarNames=file.UsedVarNames, dependencies=file.Dependencies)
