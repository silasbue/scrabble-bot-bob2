﻿namespace Bob2

open System.Text.RegularExpressions
open Microsoft.FSharp.Core
open ScrabbleLib
open ScrabbleUtil
open ScrabbleUtil.Dictionary
open ScrabbleUtil.ServerCommunication

open System.IO

open ScrabbleUtil.DebugPrint
// The RegEx module is only used to parse human input. It is not used for the final product.

module RegEx =
    open System.Text.RegularExpressions

    let (|Regex|_|) pattern input =
        let m = Regex.Match(input, pattern)
        if m.Success then Some(List.tail [ for g in m.Groups -> g.Value ])
        else None

    let parseMove ts =
        let pattern = @"([-]?[0-9]+[ ])([-]?[0-9]+[ ])([0-9]+)([A-Z]{1})([0-9]+)[ ]?"
        Regex.Matches(ts, pattern) |>
        Seq.cast<Match> |>
        Seq.map
            (fun t ->
                match t.Value with
                | Regex pattern [x; y; id; c; p] ->
                    ((x |> int, y |> int), (id |> uint32, (c |> char, p |> int)))
                | _ -> failwith "Failed (should never happen)") |>
        Seq.toList

 module Print =

    let printHand pieces hand =
        hand |>
        MultiSet.fold (fun _ x i -> forcePrint (sprintf "%d -> (%A, %d)\n" x (Map.find x pieces) i)) ()

module State =

    // Make sure to keep your state localised in this module. It makes your life a whole lot easier.
    // Currently, it only keeps track of your hand, your player numer, your board, and your dictionary,
    // but it could, potentially, keep track of other useful
    // information, such as number of players, player turn, etc.

    type state = {
        board            : Parser.board
        dict             : ScrabbleUtil.Dictionary.Dict
        playerNumber     : uint32
        playerTurn       : uint32
        hand             : MultiSet.MultiSet<uint32>
        // parsedBoard      : coord -> bool
        boardTiles       : Map<coord, char>
        isFirstMove      : bool
        amountOfPieces   : int
        numberOfPlayers  : uint32
    }

    let mkState b d pn h bt fm aof nop pt = {board = b; dict = d;  playerNumber = pn; hand = h; boardTiles = bt;  isFirstMove = fm; amountOfPieces = aof; numberOfPlayers = nop; playerTurn = pt }

    let board st          = st.board
    let dict st           = st.dict
    let playerNumber st   = st.playerNumber
    let hand st           = st.hand
    let boardTiles st     = st.boardTiles
     
    let isFirstMove st    = st.isFirstMove
    let amountOfPieces st = st.amountOfPieces
    let numberOfPlayer st = st.numberOfPlayers
    let playerTurn st = st.playerTurn

module Scrabble =
    open System.Threading
    
    let uintToChar (u: uint32) =
        match u with
        | u -> char (u + 64u)

    let charNumberToPoints (ch: int) = 
        match ch with
        | 0                                             -> 0
        | 1 | 5 | 9 | 12 | 14 | 15 | 18 | 19 | 20 | 21  -> 1
        | 4 | 7                                         -> 2
        | 2 | 3 | 13 | 16                               -> 3
        | 6 | 8 | 22 | 23 | 25                          -> 4
        | 11                                            -> 5
        | 10 | 24                                       -> 8
        | 17 | 26                                       -> 10
        | _                                             -> failwith "Not valid character index"
        
    
    let charToUint (c: char) =
        match c with
        | c -> uint32 c - 64u
        
    let charToInt (c: char) =
        match c with
        | c -> int c - 64
    
    let rec isAWord (word: string) (d: Dict) =
        // debugPrint $"checking is a word"
        match word with
        | x when x = "" -> false
        | x -> match step x[0] d with
                   | Some (b, _) when String.length word = 1 = true -> b
                   | Some (_, d) -> isAWord word[1..] d
                   | None -> false
    
    let rec canBeAWord (word: string) (d: Dict) =
        // debugPrint $"checking is a word"
        match word with
        | x when x = "" -> false
        | x -> match step x[0] d with
                   | Some _ when String.length word = 1 = true -> true
                   | Some (_, d) -> isAWord word[1..] d
                   | None -> false
                   
    let rec canFitVertical (startCoord: (int * int)) (st: State.state) (word: string) =
        let rec aux (coord: (int * int)) (st: State.state) (word: string) (up: bool) =
            let nextCoord = if up then (fst coord, snd coord - 1) else (fst coord, snd coord + 1)
            // debugPrint$"checking vertical"
            match st.boardTiles |> Map.tryFind nextCoord with
            | Some c when up -> aux nextCoord st (c.ToString() + word) up
            | Some c -> aux nextCoord st (word + c.ToString()) up
            | None when up -> aux startCoord st word false
            | None when String.length word = 1 -> true
            | None ->
                // if isAWord word st.dict then debugPrint $"word fits vertical: {word}\n" else debugPrint $"no fit"
                // if canBeAWord word st.dict then debugPrint $"canBeAWord ver: {word}\n"
                canBeAWord word st.dict
        aux startCoord st word true

    let rec canFitHorizontal (startCoord: (int * int)) (st: State.state) (word: string) =
        let rec aux (coord: (int * int)) (st: State.state) (word: string) (right: bool) =
            let nextCoord = if right then (fst coord - 1, snd coord) else (fst coord  + 1, snd coord)
            // debugPrint$"checking horizontal"
            match st.boardTiles |> Map.tryFind nextCoord with
            | Some c when right -> aux nextCoord st (c.ToString() + word) right
            | Some c -> aux nextCoord st (word + c.ToString()) right
            | None when right -> aux startCoord st word true
            | None when String.length word = 1 -> true
            | None ->
                // if isAWord word st.dict then debugPrint $"word fits horizontal: {word}\n" else debugPrint $"no fit\n"
                // if canBeAWord word st.dict then debugPrint $"canBeAWord hor: {word}\n"
                canBeAWord word st.dict
        aux startCoord st word false

        
    let rec fitsVertical (startCoord: (int * int)) (st: State.state) (word: string) =
        let rec aux (coord: (int * int)) (st: State.state) (word: string) (up: bool) =
            let nextCoord = if up then (fst coord, snd coord - 1) else (fst coord, snd coord + 1)
            // debugPrint$"checking vertical"
            match st.boardTiles |> Map.tryFind nextCoord with
            | Some c when up -> aux nextCoord st (c.ToString() + word) up
            | Some c -> aux nextCoord st (word + c.ToString()) up
            | None when up -> aux startCoord st word false
            | None when String.length word = 1 -> true
            | None ->
                // if isAWord word st.dict then debugPrint $"word fits vertical: {word}\n" else debugPrint $"no fit"
                isAWord word st.dict
        aux startCoord st word true
        
    let rec fitsHorizontal (startCoord: (int * int)) (st: State.state) (word: string) =
        let rec aux (coord: (int * int)) (st: State.state) (word: string) (left: bool) =
            let nextCoord = if left then (fst coord - 1, snd coord) else (fst coord  + 1, snd coord)
            // debugPrint$"checking horizontal"
            match st.boardTiles |> Map.tryFind nextCoord with
            | Some c when left -> aux nextCoord st (c.ToString() + word) left
            | Some c -> aux nextCoord st (word + c.ToString()) left
            | None when left -> aux startCoord st word false
            | None when String.length word = 1 -> true
            | None ->
                // if isAWord word st.dict then debugPrint $"word fits horizontal: {word}\n" else debugPrint $"no fit\n"
                isAWord word st.dict
        aux startCoord st word true
        
    let playableWordsWithCoords (letters: char list) (d: Dict) (word: string) (coord: (int * int)) (isVertical: bool) (st: State.state) =
        let isFreeBehind = if isVertical then not (Map.containsKey (fst coord, snd coord - 2) st.boardTiles) else not (Map.containsKey (fst coord - 2, snd coord) st.boardTiles)
        let rec aux (letters: char list) (d: Dict) (word: string) (playableWords: string list) (tempOut: char list) (coord: int * int) =
            let isOccupied = Map.containsKey (fst coord, snd coord) st.boardTiles
            let isFree = if isVertical
                         then not (Map.containsKey (fst coord, snd coord) st.boardTiles ||
                              Map.containsKey (fst coord + 1, snd coord) st.boardTiles ||
                              Map.containsKey (fst coord - 1, snd coord) st.boardTiles ||
                              Map.containsKey (fst coord, snd coord + 1) st.boardTiles)
                         else not (Map.containsKey (fst coord, snd coord) st.boardTiles ||
                              Map.containsKey (fst coord, snd coord + 1) st.boardTiles ||
                              Map.containsKey (fst coord, snd coord - 1) st.boardTiles ||
                              Map.containsKey (fst coord + 1, snd coord) st.boardTiles)
            let isFreeInFront = if isVertical
                                then not (Map.containsKey (fst coord, snd coord + 1) st.boardTiles)
                                else not (Map.containsKey (fst coord + 1, snd coord) st.boardTiles)
            
            if not (List.isEmpty letters)
            then 
                // debugPrint $"isFree: {isFree}\n"
                // debugPrint $"loading... {letters.Head.ToString()}, {coord}"
                // debugPrint $"fitsVertical: {fitsVertical coord st (letters.Head.ToString())}\n"
                // debugPrint $"fitsHorizontal: {fitsHorizontal coord st (letters.Head.ToString())}\n"
            
                let fitCheck =
                    if isVertical 
                    then
                        if isFreeInFront
                        then fitsHorizontal coord st (letters.Head.ToString())
                        else fitsHorizontal coord st (letters.Head.ToString()) && fitsVertical coord st (if (st.boardTiles |> Map.containsKey (fst coord, snd coord - 1)) then (letters.Head.ToString()) else word)
                    else
                        if isFreeInFront
                        then fitsVertical coord st (letters.Head.ToString())
                        else fitsVertical coord st (letters.Head.ToString()) && fitsHorizontal coord st (if (st.boardTiles |> Map.containsKey (fst coord - 1, snd coord)) then (letters.Head.ToString()) else word)
                
                if (not isOccupied) && fitCheck && isFreeBehind
                then
                    // debugPrint $"Matching..."
                    match letters with
                    | x::xs -> match step x d with
                               | Some (b, d) when b = true -> aux (xs @ tempOut) d (word + x.ToString()) (playableWords |> List.append [word + x.ToString()]) [] (if isVertical then (fst coord, snd coord + 1) else (fst coord + 1, snd coord))
                               | Some (_, d) -> aux (xs @ tempOut) d (word + x.ToString()) playableWords [] (if isVertical then (fst coord, snd coord + 1) else (fst coord + 1, snd coord))
                               | None -> aux xs d word playableWords (tempOut @ [x]) coord
                    | [] -> playableWords
                else
                    // debugPrint $"CHECK failed: {playableWords}"
                    playableWords
            else playableWords
        letters |> List.fold (fun acc x -> acc @ (aux ([x] @ (List.except [x] letters)) d word [] [] coord)) []
        
    let playableWords (letters: char list) (d: Dict) (word: string) =
        let rec aux (letters: char list) (d: Dict) (word: string) (playableWords: string list) (tempOut: char list) =
            match letters with
            | x::xs -> match step x d with
                       | Some (b, d) when b = true -> aux (xs @ tempOut) d (word + x.ToString())  (playableWords |> List.append [word + x.ToString()]) []
                       | Some (_, d) -> aux (xs @ tempOut) d (word + x.ToString()) playableWords []
                       | None -> aux xs d word playableWords (tempOut @ [x])
            | [] -> playableWords            
            
        letters |> List.fold (fun acc x -> acc @ (aux ([x] @ (List.except [x] letters)) d word [] [])) []

    

    let playableWordsWithPrefix (prefixChar: char) (letters: char list) (d: Dict) =
        match step prefixChar d with
        | Some (_, d) -> playableWords letters d (prefixChar.ToString())
        | None -> failwith "None"
        
    let quickStep s d =
        match step s d with
        | Some (_, d) -> d
        | None -> d
     
    let playableWord (letters: char list) (d: Dict) =
        let rec aux (letters: char list) (d: Dict) (word: string) =
            match letters with 
            | x::xs -> match step x d with
                       | Some (b, d) when b = true -> (word + x.ToString())
                       | Some (b, d) -> aux xs d (word + x.ToString())
                       | None -> aux (xs @ [x]) d word
            | [] -> failwith "todo"
        aux letters d ""
        
    let playableWordWithPrefix (prefixChar: char) (letters: char list) (d: Dict) =
        match step prefixChar d with
        | Some (_, d) -> playableWord letters d
        | None -> failwith "None"

                   
    let rec wordToFirstMove (word: string) (coord: int * int) output =
         match word with
         | s when s <> "" -> wordToFirstMove s[1..] (fst coord, (snd coord + 1)) $"{output} {fst coord} {snd coord} {charToInt s[0]}{s[0]}{charNumberToPoints (charToInt s[0])}"
         | "" -> RegEx.parseMove output

    let rec wordToMove (word: string) (coord : int * int) (isVertical: bool) output =
        match word with
        | s when s <> "" && isVertical -> wordToMove s[1..] (fst coord, (snd coord + 1)) true  $"{output} {fst coord} {snd coord} {charToInt s[0]}{s[0]}{charNumberToPoints (charToInt s[0])}"
        | s when s <> "" -> wordToMove s[1..] (fst coord + 1, (snd coord)) false  $"{output} {fst coord} {snd coord} {charToInt s[0]}{s[0]}{charNumberToPoints (charToInt s[0])}"
        | "" -> RegEx.parseMove output

    let checkRight (st: State.state) =
        st.boardTiles |> Map.filter (fun x _ -> 
            not ((st.boardTiles.ContainsKey (fst x + 1, (snd x)) ||
            st.boardTiles.ContainsKey (fst x + 1, (snd x + 1)) ||
            st.boardTiles.ContainsKey (fst x + 1, (snd x - 1))) || 
            st.boardTiles.ContainsKey (fst x - 1, (snd x)) ||
            st.boardTiles.ContainsKey (fst x + 2, (snd x)) ||
            st.boardTiles.ContainsKey (fst x + 2, (snd x + 1)) ||
            st.boardTiles.ContainsKey (fst x + 2, (snd x - 1))))

    let checkDown (st: State.state) = 
        st.boardTiles |> Map.filter (fun x _ ->
        not(st.boardTiles.ContainsKey (fst x, (snd x + 1)) ||
            st.boardTiles.ContainsKey (fst x - 1, (snd x + 1)) ||
            st.boardTiles.ContainsKey (fst x + 1, (snd x + 1)) ||
            st.boardTiles.ContainsKey (fst x, (snd x - 1)) ||
            st.boardTiles.ContainsKey (fst x, (snd x + 2)) ||
            st.boardTiles.ContainsKey (fst x + 1, (snd x + 2)) ||
            st.boardTiles.ContainsKey (fst x - 1, (snd x + 2))))

    
    let findDownMoves (st: State.state) (letters: char list) =
        (checkDown st)
        |> Map.fold (fun acc k v -> acc @ [((fst k, snd k + 1) ,playableWordsWithPrefix v letters st.dict)]) []
        |> List.fold (fun acc (c, x) -> acc @ [(c ,x |> List.fold (fun acc x -> acc @ [x[1..]]) [])]) []
        |> List.filter (fun (_, x) -> not (List.isEmpty x))
    let findRightMoves (st: State.state) (letters: char list) =
        (checkRight st)
        |> Map.fold (fun acc k v -> acc @ [((fst k + 1, snd k) ,playableWordsWithPrefix v letters st.dict)]) []
        |> List.fold (fun acc (c, x) -> acc @ [(c ,x |> List.fold (fun acc x -> acc @ [x[1..]]) [])]) []
        |> List.filter (fun (_, x) -> not (List.isEmpty x))
    
    let findDownMovesCoords (st: State.state) (letters: char list) =
        st.boardTiles
        |> Map.fold (fun acc k v -> acc @ [((fst k, snd k + 1) ,playableWordsWithCoords letters (quickStep v st.dict) (v.ToString()) (fst k, snd k + 1) true st)]) []
        |> List.fold (fun acc (c, x) -> acc @ [(c ,x |> List.fold (fun acc x -> acc @ [x[1..]]) [])]) []
        |> List.filter (fun (_, x) -> not (List.isEmpty x))
    let findRightMovesCoords (st: State.state) (letters: char list) =
        (checkRight st)
        |> Map.fold (fun acc k v -> acc @ [((fst k + 1, snd k) ,playableWordsWithCoords letters (quickStep v st.dict) (v.ToString()) (fst k + 1, snd k) false st)]) []
        |> List.fold (fun acc (c, x) -> acc @ [(c ,x |> List.fold (fun acc x -> acc @ [x[1..]]) [])]) []
        |> List.filter (fun (_, x) -> not (List.isEmpty x))

    let playGame cstream pieces (st : State.state) =
        let rec aux (st : State.state) =
            // Print.printHand pieces (State.hand st)

            // remove the force print when you move on from manual input (or when you have learnt the format)
            // forcePrint "Input move (format '(<x-coordinate> <y-coordinate> <piece id><character><point-value> )*', note the absence of space between the last inputs)\n\n"
            // debugPrint $"current state: {st}\n\n"
            // debugPrint $"squares: {st.board.squares}\n\n"
            // debugPrint $"hand multiset: {st.hand}\n\n"
            
            let chList = MultiSet.toList st.hand |> List.fold (fun acc x -> acc @ [uintToChar x]) []
            
            // debugPrint $"chList: {chList}\n\n"
            let check2 = checkRight st
            let check3 = checkDown st
            // debugPrint $"check: {check2}\n\n {check3}\n\n"

            // TODO IMPLEMENT MOVE
            // let move =
            //     match Map.isEmpty st.boardTiles with
            //     | true -> playableWord chList st.dict ""
            //     | false -> 
            
            let passMove = [((0, 0), (0u, (' ', 0)))]
            
            let firstMoves = playableWords chList st.dict ""
            // debugPrint $"Moves: {firstMoves}\n\n"
            
            debugPrint $"findDownMoves: {findDownMovesCoords st chList} \n\n"
            debugPrint $"findRightMoves: {findRightMovesCoords st chList} \n\n"
            
            // let move = RegEx.parseMove input
            // let firstMove = wordToMove moves[0] (0,0) false ""
            // debugPrint $"First move: {firstMove}\n\n"
            let findLongestWord (lst: string list) =
                let rec aux (lst: string list) (longest: string) =
                    match lst with
                    | x::xs when x.Length > longest.Length -> aux xs x
                    | x::xs -> aux xs longest
                    | [] -> longest
                aux lst ""

            let findLongestWord2 (lst) =
                let rec aux (lst) (longest: ((int * int) * string) ) =
                    match lst with
                    | (x, y)::xs when String.length (findLongestWord y) > String.length (snd longest) -> aux xs (x, findLongestWord y)
                    | (x, y)::xs -> aux xs longest
                    | [] -> longest
                aux lst ((0,0), "")
            
            let otherMove = 
                match findDownMovesCoords st chList with
                | (x, y)::xs -> wordToMove (snd (findLongestWord2 ([(x, y)] @ xs))) (fst (findLongestWord2 ([(x, y)] @ xs))) true ""
                | [] -> match findRightMovesCoords st chList with
                        | (x, y)::xs -> wordToMove (snd (findLongestWord2 ([(x, y)] @ xs))) (fst (findLongestWord2 ([(x, y)] @ xs))) false ""
                        | [] -> passMove
            // debugPrint $"otherMove: {otherMove}\n"

            let firstMove =
                match firstMoves with
                | x::xs -> wordToMove (findLongestWord ([x] @ xs)) (0,0) false ""
                | [] -> passMove

            let move = if st.isFirstMove then firstMove else otherMove

            // debugPrint $"First move: {firstMove} \n\n"
            // debugPrint $"Other move: {otherMove} \n\n"
            debugPrint $"Next move: {move} \n\n"
            // let input =  System.Console.ReadLine()

            debugPrint (sprintf "Player %d -> Server:\n%A\n" (State.playerNumber st) move) // keep the debug lines. They are useful.
            debugPrint $"amount of pieces left: {st.amountOfPieces}\n"
            
            if st.playerNumber = st.playerTurn
            then
                if move = passMove then  (if st.amountOfPieces >= (14 * (int st.numberOfPlayers)) then send cstream (SMChange (MultiSet.toList st.hand)) else send cstream SMPass) else send cstream (SMPlay move)
                
            
            
            //if st.isFirstMove then (if List.isEmpty firstMove then cstream SMPass send cstream (SMPlay move) else cstream SMPass send cstream (SMPlay move))

            
            let msg = recv cstream
            debugPrint (sprintf "Player %d <- Server:\n%A\n" (State.playerNumber st) move) // keep the debug lines. They are useful.

            match msg with
            | RCM (CMPlaySuccess(ms, points, newPieces)) ->
                (* Successful play by you. Update your state (remove old tiles, add the new ones, change turn, etc) *)
                debugPrint $"\n\nMOVES: {ms}\nPOINTS: {points}\nNEWPIECES: {newPieces}\n\n BOARDTILES: {st.boardTiles}\n\n"
                // hand after removing used pieces
                let removedHand = ms |> List.fold (fun acc x -> MultiSet.removeSingle (fst (snd x)) acc) st.hand
                // new hand after adding new pieces
                let newHand = newPieces |> List.fold (fun acc x -> MultiSet.add (fst x) (snd x) acc) removedHand
                // forcePrint $"newpieces: {newPieces}\n"
                // board after adding word
                let newBoardTiles = ms |> List.fold (fun acc x -> Map.add (fst x) (fst (snd (snd x))) acc) st.boardTiles
                
                let newPt = ((st.playerTurn) % st.numberOfPlayers) + 1u
                
                let st' = State.mkState st.board st.dict st.playerNumber newHand newBoardTiles false (st.amountOfPieces - List.length ms) st.numberOfPlayers newPt// This state needs to be updated

                aux st'
            | RCM (CMPlayed (pid, ms, points)) ->
                (* Successful play by other player. Update your state *)
                let newBoardTiles = ms |> List.fold (fun acc x -> Map.add (fst x) (fst (snd (snd x))) acc) st.boardTiles
                let newPt = ((st.playerTurn) % st.numberOfPlayers) + 1u
                let st' = State.mkState st.board st.dict st.playerNumber st.hand newBoardTiles false (st.amountOfPieces - List.length ms) st.numberOfPlayers newPt
                aux st'
            | RCM (CMPlayFailed (pid, ms)) ->
                let newPt = ((st.playerTurn) % st.numberOfPlayers) + 1u
                let st' = State.mkState st.board st.dict st.playerNumber st.hand st.boardTiles false st.amountOfPieces st.numberOfPlayers newPt
                aux st'
            | RCM (CMGameOver _) -> ()
            | RCM (CMPassed pid) ->
                let newPt = ((st.playerTurn) % st.numberOfPlayers) + 1u
                let st' = State.mkState st.board st.dict st.playerNumber st.hand st.boardTiles false st.amountOfPieces st.numberOfPlayers newPt
                aux st'
            | RCM (CMChangeSuccess newPieces) -> 
                let removedHand = (MultiSet.toList st.hand) |> List.fold (fun acc x -> MultiSet.removeSingle x acc) st.hand

                let newHand = newPieces |> List.fold (fun acc x -> MultiSet.add (fst x) (snd x) acc) removedHand
                // forcePrint $"newpieces: {newPieces}\n"
                let newPt = ((st.playerTurn) % st.numberOfPlayers) + 1u
                let st' = State.mkState st.board st.dict st.playerNumber newHand st.boardTiles false st.amountOfPieces st.numberOfPlayers newPt// This state needs to be updated
                aux st'
            | RCM (CMChange _) ->
                let newPt = ((st.playerTurn) % st.numberOfPlayers) + 1u
                let st' = State.mkState st.board st.dict st.playerNumber st.hand st.boardTiles false st.amountOfPieces st.numberOfPlayers newPt// This state needs to be updated
                aux st'
                
            | RCM a -> failwith (sprintf "not implmented: %A" a)
            | RGPE err -> printfn "Gameplay Error:\n%A" err; aux st

        aux st

    
    let startGame
            (boardP : boardProg)
            (dictf : bool -> Dictionary.Dict)
            (numPlayers : uint32)
            (playerNumber : uint32)
            (playerTurn  : uint32)
            (hand : (uint32 * uint32) list)
            (tiles : Map<uint32, tile>)
            (timeout : uint32 option)
            (cstream : Stream) =
        debugPrint
            (sprintf "Starting game!
                      number of players = %d
                      player id = %d
                      player turn = %d
                      hand =  %A
                      timeout = %A\n\n" numPlayers playerNumber playerTurn hand timeout)

        //let dict = dictf true // Uncomment if using a gaddag for your dictionary
        let dict = dictf false // Uncomment if using a trie for your dictionary
        let board = Parser.mkBoard boardP

        let handSet = List.fold (fun acc (x, k) -> MultiSet.add x k acc) MultiSet.empty hand
        let boardTiles = Map.empty

        fun () -> playGame cstream tiles (State.mkState board dict playerNumber handSet boardTiles true 104 numPlayers playerTurn)

