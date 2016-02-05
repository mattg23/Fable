namespace Fabel.AST.Fabel
open Fabel.AST

(** ##Decorators *)
type Decorator =
    | Decorator of fullName: string * args: obj list
    member x.FullName = match x with Decorator (prop,_) -> prop
    member x.Arguments = match x with Decorator (_,prop) -> prop
    member x.Name =
        x.FullName.Substring (x.FullName.LastIndexOf '.' + 1)

(** ##Types *)
type PrimitiveTypeKind =
    | Unit // unit, null, undefined (non-strict equality)
    | Number of NumberKind
    | String
    | Regex
    | Boolean
    | Function of arity: int
    | Array of ArrayKind

and Type =
    | UnknownType
    | DeclaredType of Entity
    | PrimitiveType of PrimitiveTypeKind

(** ##Entities *)
and EntityLocation = { file: string; fullName: string }

and EntityKind =
    | Module
    | Class of baseClass: EntityLocation option
    | Union
    | Record    
    | Interface

and Entity(kind, file, fullName, interfaces, decorators, isPublic) =
    member x.Kind: EntityKind = kind
    member x.File: string option = file
    member x.FullName: string = fullName
    member x.Interfaces: string list = interfaces
    member x.Decorators: Decorator list = decorators
    member x.IsPublic: bool = isPublic
    member x.Name =
        x.FullName.Substring(x.FullName.LastIndexOf('.') + 1)
    member x.Namespace =
        let fullName = x.FullName
        match fullName.LastIndexOf "." with
        | -1 -> ""
        | 0 -> failwithf "Unexpected entity full name: %s" fullName
        | _ as i -> fullName.Substring(0, i)
    member x.TryGetDecorator decorator =
        decorators |> List.tryFind (fun x -> x.Name = decorator)
    static member CreateRootModule fileName =
        Entity (Module, Some fileName, "", [], [], true)
    override x.ToString() = sprintf "%s %A" x.Name kind

and Declaration =
    | ActionDeclaration of Expr
    | EntityDeclaration of Entity * nested: Declaration list * range: SourceLocation
    | MemberDeclaration of Member

and MemberKind =
    | Constructor
    | Method of name: string
    | Getter of name: string
    | Setter of name: string

and Member(kind, range, args, body, decorators, isPublic, isStatic) =
    member x.Kind: MemberKind = kind
    member x.Range: SourceLocation = range
    member x.Arguments: Ident list = args
    member x.Body: Expr = body
    member x.Decorators: Decorator list = decorators
    member x.IsPublic: bool = isPublic
    member x.IsStatic: bool = isStatic
    member x.TryGetDecorator decorator =
        decorators |> List.tryFind (fun x -> x.Name = decorator)
    override x.ToString() = sprintf "%A" kind
        
and ExternalEntity =
    | ImportModule of fullName: string * moduleName: string * isNs: bool
    | GlobalModule of fullName: string
    member x.FullName =
        match x with ImportModule (fullName, _, _)
                   | GlobalModule fullName -> fullName
    
and File(fileName, root, decls, extEntities) =
    member x.FileName: string = fileName
    member x.Root: Entity = root
    member x.Declarations: Declaration list = decls
    member x.ExternalEntities: ExternalEntity list = extEntities
    
(** ##Expressions *)
and ArrayKind = TypedArray of NumberKind | DynamicArray | Tuple

and ApplyInfo = {
        methodName: string
        ownerFullName: string
        callee: Expr option
        args: Expr list
        returnType: Type
        range: SourceLocation option
        decorators: Decorator list
        calleeTypeArgs: Type list
        methodTypeArgs: Type list
    }
    
and ApplyKind =
    | ApplyMeth | ApplyGet | ApplyCons

and Ident = { name: string; typ: Type }

and ValueKind =
    | Null
    | This of Type
    | Super of Type
    | TypeRef of Type
    | IdentValue of Ident
    | ImportRef of import: string * isNs: bool * prop: string option
    | NumberConst of U2<int,float> * NumberKind
    | StringConst of string
    | BoolConst of bool
    | RegexConst of source: string * flags: RegexFlag list
    | ArrayConst of args: U2<Expr list, int> * kind: ArrayKind
    | UnaryOp of UnaryOperator
    | BinaryOp of BinaryOperator
    | LogicalOp of LogicalOperator
    | Lambda of args: Ident list * body: Expr
    | Emit of string
    member x.Type =
        match x with
        | Null -> PrimitiveType Unit
        | This typ | Super typ | IdentValue {typ=typ} -> typ
        | ImportRef _ | TypeRef _ | Emit _ -> UnknownType
        | NumberConst (_,kind) -> PrimitiveType (Number kind)
        | StringConst _ -> PrimitiveType String
        | RegexConst _ -> PrimitiveType Regex
        | BoolConst _ -> PrimitiveType Boolean
        | ArrayConst (_,kind) -> PrimitiveType (Array kind)
        | UnaryOp _ -> PrimitiveType (Function 1)
        | BinaryOp _ | LogicalOp _ -> PrimitiveType (Function 2)
        | Lambda (args, _) -> PrimitiveType (Function args.Length)
    
and LoopKind =
    | While of guard: Expr * body: Expr
    | For of ident: Ident * start: Expr * limit: Expr * body: Expr * isUp: bool
    | ForOf of ident: Ident * enumerable: Expr * body: Expr
    
and Expr =
    // Pure Expressions
    | Value of value: ValueKind
    | ObjExpr of members: (string*Expr) list * range: SourceLocation option
    | IfThenElse of guardExpr: Expr * thenExpr: Expr * elseExpr: Expr * range: SourceLocation option
    | Apply of callee: Expr * args: Expr list * kind: ApplyKind * typ: Type * range: SourceLocation option

    // Pseudo-Statements
    | Throw of Expr * range: SourceLocation option
    | Loop of LoopKind * range: SourceLocation option
    | VarDeclaration of var: Ident * value: Expr * isMutable: bool
    | Set of callee: Expr * property: Expr option * value: Expr * range: SourceLocation option
    | Sequential of Expr list * range: SourceLocation option
    | TryCatch of body: Expr * catch: (Ident * Expr) option * finalizer: Expr option * range: SourceLocation option

    // This is mainly to hide the type of ignored expressions so they don't trigger
    // a return in functions, but they'll be erased in compiled code
    | Wrapped of Expr * Type

    member x.Type =
        match x with
        | Value kind -> kind.Type 
        | ObjExpr _ -> UnknownType
        | Wrapped (_,typ) | Apply (_,_,_,typ,_) -> typ
        | IfThenElse (_,thenExpr,_,_) -> thenExpr.Type
        | Throw _ | Loop _ | Set _ | VarDeclaration _ -> PrimitiveType Unit
        | Sequential (exprs,_) ->
            match exprs with
            | [] -> PrimitiveType Unit
            | exprs -> (Seq.last exprs).Type
        | TryCatch (body,_,finalizer,_) ->
            match finalizer with
            | Some _ -> PrimitiveType Unit
            | None -> body.Type
            
    member x.Range: SourceLocation option =
        match x with
        | Value _ -> None
        | VarDeclaration (_,e,_) | Wrapped (e,_) -> e.Range
        | ObjExpr (_,range) 
        | Apply (_,_,_,_,range)
        | IfThenElse (_,_,_,range)
        | Throw (_,range)
        | Loop (_,range)
        | Set (_,_,_,range)
        | Sequential (_,range)
        | TryCatch (_,_,_,range) -> range
            
    // member x.Children: Expr list =
    //     match x with
    //     | Value _ -> []
    //     | ObjExpr (decls,_) -> decls |> List.map snd
    //     | Get (callee,prop,_) -> [callee; prop]
    //     | Emit (_,args,_,_) -> args
    //     | Apply (callee,args,_,_,_) -> (callee::args)
    //     | IfThenElse (guardExpr,thenExpr,elseExpr,_) -> [guardExpr; thenExpr; elseExpr]
    //     | Throw (ex,_) -> [ex]
    //     | Loop (kind,_) ->
    //         match kind with
    //         | While (guard,body) -> [guard; body]
    //         | For (_,start,limit,body,_) -> [start; limit; body]
    //         | ForOf (_,enumerable,body) -> [enumerable; body]
    //     | Set (callee,prop,value,_) ->
    //         match prop with
    //         | Some prop -> [callee; prop; value]
    //         | None -> [callee; value]
    //     | VarDeclaration (_,value,_) -> [value]
    //     | Sequential (exprs,_) -> exprs
    //     | Wrapped (e,_) -> [e]    
    //     | TryCatch (body,catch,finalizer,_) ->
    //         match catch, finalizer with
    //         | Some (_,catch), Some finalizer -> [body; catch; finalizer]
    //         | Some (_,catch), None -> [body; catch]
    //         | None, Some finalizer -> [body; finalizer]
    //         | None, None -> [body]
    
module Util =
    open Fabel
    
    type CallKind =
        | InstanceCall of callee: Expr * meth: string * args: Expr list
        | ImportCall of importRef: string * isNs: bool * modName: string option * meth: string option * isCons: bool * args: Expr list
        | CoreLibCall of modName: string * meth: string option * isCons: bool * args: Expr list
        | GlobalCall of modName: string * meth: string option * isCons: bool * args: Expr list

    let makeLoop range loopKind = Loop (loopKind, range)
    let makeTypeRef typ = Value (TypeRef typ)
    let makeCoreRef com modname =
        Value (ImportRef (Naming.getCoreLibPath com, true, Some modname))

    let makeIdent name: Ident = {name=name; typ=UnknownType}
    let makeIdentExpr name = makeIdent name |> IdentValue |> Value 

    let makeBinOp, makeUnOp, makeLogOp, makeEqOp, makeNoStrictEqOp, makeNeqOp, makeNoStrictNeqOp =
        let makeOp range typ args op =
            Apply (Value op, args, ApplyMeth, typ, range)
        (fun range typ args op -> makeOp range typ args (BinaryOp op)),
        (fun range typ args op -> makeOp range typ args (UnaryOp op)),
        (fun range args op -> makeOp range (PrimitiveType Boolean) args (LogicalOp op)),
        (fun range args -> makeOp range (PrimitiveType Boolean) args (BinaryOp BinaryEqualStrict)),
        (fun range args -> makeOp range (PrimitiveType Boolean) args (BinaryOp BinaryEqual)),
        (fun range args -> makeOp range (PrimitiveType Boolean) args (BinaryOp BinaryUnequalStrict)),
        (fun range args -> makeOp range (PrimitiveType Boolean) args (BinaryOp BinaryUnequal))

    let rec makeSequential range statements =
        match statements with
        | [] -> Value Null
        | [expr] -> expr
        | first::rest ->
            match first, rest with
            | Value Null, _ -> makeSequential range rest
            | _, [Sequential (statements, _)] -> makeSequential range (first::statements)
            // Calls to System.Object..ctor in class constructors
            // TODO: Remove also calls to System.Exception..ctor in constructors?
            // TODO: Move these optimizations to Fabel2Babel layer? (remove also Null as last expr)
            | ObjExpr ([],_), _ -> makeSequential range rest
            | _ -> Sequential (statements, range)
                
    let makeConst (value: obj) =
        match value with
        | :? bool as x -> BoolConst x
        | :? string as x -> StringConst x
        // Integer types
        | :? int as x -> NumberConst (U2.Case1 x, Int32)
        | :? byte as x -> NumberConst (U2.Case1 (int x), UInt8Clamped)
        | :? sbyte as x -> NumberConst (U2.Case1 (int x), Int8)
        | :? int16 as x -> NumberConst (U2.Case1 (int x), Int16)
        | :? uint16 as x -> NumberConst (U2.Case1 (int x), UInt16)
        | :? char as x -> NumberConst (U2.Case1 (int x), UInt16)
        | :? uint32 as x -> NumberConst (U2.Case1 (int x), UInt32)
        // Float types
        | :? float as x -> NumberConst (U2.Case2 x, Float64)
        | :? int64 as x -> NumberConst (U2.Case2 (float x), Float64)
        | :? uint64 as x -> NumberConst (U2.Case2 (float x), Float64)
        | :? float32 as x -> NumberConst (U2.Case2 (float x), Float32)
        // TODO: Regex
        | :? unit | _ when value = null -> Null
        | _ -> failwithf "Unexpected literal %O" value
        |> Value
        
    let makeFnType args =
        PrimitiveType (List.length args |> Function)
    
    let makeGet range typ callee propExpr =
        Apply (callee, [propExpr], ApplyGet, typ, range)
        
    let makeCall com range typ kind =
        let getCallee meth args owner =
            match meth with
            | None -> owner
            | Some meth ->
                let fnTyp = PrimitiveType (List.length args |> Function)
                Apply (owner, [makeConst meth], ApplyGet, fnTyp, None)
        let apply kind args callee =
            Apply(callee, args, kind, typ, range)
        let getKind isCons =
            if isCons then ApplyCons else ApplyMeth
        match kind with
        | InstanceCall (callee, meth, args) ->
            let fnTyp = PrimitiveType (List.length args |> Function)
            Apply (callee, [makeConst meth], ApplyGet, fnTyp, None)
            |> apply ApplyMeth args
        | ImportCall (importRef, isNs, modOption, meth, isCons, args) ->
            Value (ImportRef (importRef, isNs, modOption))
            |> getCallee meth args
            |> apply (getKind isCons) args
        | CoreLibCall (modName, meth, isCons, args) ->
            makeCoreRef com modName
            |> getCallee meth args
            |> apply (getKind isCons) args
        | GlobalCall (modName, meth, isCons, args) ->
            makeIdentExpr modName
            |> getCallee meth args
            |> apply (getKind isCons) args
            
    let makeTypeTest com range (typ: Type) expr =
        let stringType, boolType =
            PrimitiveType String, PrimitiveType Boolean
        let checkType (primitiveType: string) expr =
            let typof = makeUnOp None stringType [expr] UnaryTypeof
            makeBinOp range boolType [typof; makeConst primitiveType] BinaryEqualStrict
        match typ with
        | PrimitiveType kind ->
            match kind with
            | String _ -> checkType "string" expr
            | Number _ -> checkType "number" expr
            | Boolean -> checkType "boolean" expr
            | Unit ->
                makeBinOp range boolType [expr; Value Null] BinaryEqual
            | _ -> failwithf "Unsupported type test: %A" typ
        | DeclaredType typEnt ->
            match typEnt.Kind with
            | Interface ->
                // TODO: Test
                CoreLibCall ("Util", Some "hasInterface", false, [makeConst typEnt.FullName])
                |> makeCall com range boolType 
            | _ ->
                makeBinOp range boolType [expr; makeTypeRef typ] BinaryInstanceOf 
        | _ -> failwithf "Unsupported type test in %A: %A" range typ

    let makeUnionCons range =
        let args: Ident list = [makeIdent "t"; makeIdent "d"]
        let emit = Emit "this.tag=t;this.data=d;" |> Value
        let body = Apply (emit, [], ApplyMeth, PrimitiveType Unit, None)
        Member(Constructor, range, args, body, [], true, false)
        |> MemberDeclaration
        
    let makeDelegate (expr: Expr) =
        let rec flattenLambda accArgs = function
            | Value (Lambda (args, body)) ->
                flattenLambda (accArgs@args) body
            | _ as body ->
                Value (Lambda (accArgs, body))
        match expr, expr.Type with
        | Value (Lambda (args, body)), _ ->
            flattenLambda args body
        | _, PrimitiveType (Function arity) ->
            let lambdaArgs =
                [1..arity] |> List.map (fun i -> {name=sprintf "$arg%i" i; typ=UnknownType}) 
            let lambdaBody = 
                (expr, lambdaArgs)
                ||> List.fold (fun callee arg ->
                    Apply (callee, [Value (IdentValue arg)], ApplyMeth, UnknownType, expr.Range))
            Lambda (lambdaArgs, lambdaBody) |> Value
        | _ ->
            expr // Do nothing
            
    let makeApply range typ callee exprs =
        let lasti = (List.length exprs) - 1
        ((0, callee), exprs)
        ||> List.fold (fun (i, callee) expr ->
            let typ = if i = lasti then typ else PrimitiveType (Function <|i+1)
            let callee =
                match callee with
                | Sequential _ ->
                    // Surround with a lambda
                    Apply (Lambda ([], callee) |> Value, [], ApplyMeth, typ, range)
                | _ -> callee
            i, Apply (callee, [expr], ApplyMeth, typ, range))
        |> snd
