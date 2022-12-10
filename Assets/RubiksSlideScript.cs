using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;
using Rnd = UnityEngine.Random;
using KModkit;
using System.Threading;

public class RubiksSlideScript : MonoBehaviour
{
    public KMBombModule Module;
    public KMBombInfo BombInfo;
    public KMAudio Audio;

    private int _moduleId;
    private static int _moduleIdCounter = 1;
    private bool _moduleSolved;

    public class RubiksSlidePuzzle : IEquatable<RubiksSlidePuzzle>
    {
        public readonly int[] Cells;

        public RubiksSlidePuzzle(int[] cells)
        {
            Cells = cells;
        }

        public bool Equals(RubiksSlidePuzzle other)
        {
            return other != null && other.Cells.SequenceEqual(Cells);
        }

        public override bool Equals(object obj)
        {
            return obj is RubiksSlidePuzzle && Equals((RubiksSlidePuzzle)obj);
        }

        public override int GetHashCode()
        {
            return Cells.Aggregate(47, (p, n) => p * 31 + n);
        }

        public int this[int index] { get { return Cells[index]; } }

        private static readonly int[][] _shiftIxs = new[] { new[] { 3, 4, 5, 6, 7, 8, 0, 1, 2 }, new[] { 2, 0, 1, 5, 3, 4, 8, 6, 7 }, new[] { 6, 7, 8, 0, 1, 2, 3, 4, 5 }, new[] { 1, 2, 0, 4, 5, 3, 7, 8, 6 } };
        private static readonly int[][] _rotateBigIxs = new[] { new[] { 6, 3, 0, 7, 4, 1, 8, 5, 2 }, new[] { 2, 5, 8, 1, 4, 7, 0, 3, 6 } };
        private static readonly int[][] _rotateSmallIxs = new[] { new[] { 3, 0, 1, 6, 4, 2, 7, 8, 5 }, new[] { 1, 2, 5, 0, 4, 8, 3, 6, 7 } };

        public RubiksSlidePuzzle Shift(int dir)
        {
            return Transformation(_shiftIxs[dir]);
        }
        public RubiksSlidePuzzle RotateBig(int dir)
        {
            return Transformation(_rotateBigIxs[dir]);
        }
        public RubiksSlidePuzzle RotateSmall(int dir)
        {
            return Transformation(_rotateSmallIxs[dir]);
        }
        public RubiksSlidePuzzle Transformation(int[] transformation)
        {
            return new RubiksSlidePuzzle(Enumerable.Range(0, 9).Select(i => Cells[transformation[i]]).ToArray());
        }
        public RubiksSlidePuzzle ShufflePuzzle(int stage)
        {
            var puzzle = this;
            tryagain:
            int rnd = Rnd.Range(6, 12);
            for (int i = 0; i < rnd; i++)
                switch (Rnd.Range(0, 2))
                {
                    case 0: puzzle = puzzle.Shift(Rnd.Range(0, 4)); break;
                    case 1: puzzle = stage < 3 ? puzzle.RotateBig(Rnd.Range(0, 2)) : puzzle.RotateSmall(Rnd.Range(0, 2)); break;
                }
            if (puzzle.Equals(this))
                goto tryagain;
            return puzzle;
        }

        public override string ToString()
        {
            return Cells.Join(", ");
        }
    }

    public KMSelectable[] ShiftSels;
    public KMSelectable[] RotateSels;
    public KMSelectable CenterSel;
    public MeshRenderer[] ScreenRenderers;
    public Material[] PuzzleMats;
    public MeshRenderer[] TimerLights;
    public Material[] TimerMats;
    public GameObject ModelObj;

    private int _stage;
    private bool _isTransitioning = true;
    private bool _soundPlaying = true;

    private RubiksSlidePuzzle _currentPuzzle;
    private RubiksSlidePuzzle _solutionPuzzle;

    private Coroutine _transformCoroutine;

    private void Start()
    {
        _moduleId = _moduleIdCounter++;
        Module.OnActivate += Activate;
        
        Debug.LogFormat("[Rubik's Slide #{0}] Pressing any edge arrow will shift the puzzle in that direction.", _moduleId);
        Debug.LogFormat("[Rubik's Slide #{0}] Pressing any corner arrow will rotate the puzzle in that direction.", _moduleId);

        for (int i = 0; i < ShiftSels.Length; i++)
        {
            ShiftSels[i].OnInteract += ShiftPress(i);
            ShiftSels[i].OnInteractEnded += ShiftRelease(i);
        }
        for (int i = 0; i < RotateSels.Length; i++)
        {
            RotateSels[i].OnInteract += RotatePress(i / 4);
            RotateSels[i].OnInteractEnded += RotateRelease(i / 4);
        }
        CenterSel.OnInteract += CenterPress;
        CenterSel.OnInteractEnded += CenterRelease;
    }

    private void Activate()
    {
        GeneratePuzzle(_stage);
        UpdateVisuals(_currentPuzzle);
    }

    private bool CenterPress()
    {
        if (_moduleSolved || _isTransitioning)
            return false;
        Audio.PlaySoundAtTransform("Stage_Load", transform);
        UpdateVisuals(_solutionPuzzle);
        return false;
    }

    private void CenterRelease()
    {
        if (_moduleSolved || _isTransitioning)
            return;
        UpdateVisuals(_currentPuzzle);
    }

    private KMSelectable.OnInteractHandler ShiftPress(int i)
    {
        return delegate ()
        {
            if (_transformCoroutine != null)
                StopCoroutine(_transformCoroutine);
            _transformCoroutine = StartCoroutine(ShiftModel(i, true));
            if (_moduleSolved || _isTransitioning)
                return false;
            Audio.PlaySoundAtTransform("Slide", transform);
            _currentPuzzle = _currentPuzzle.Shift(i);
            UpdateVisuals(_currentPuzzle);
            CheckValidity();
            return false;
        };
    }

    private KMSelectable.OnInteractHandler RotatePress(int i)
    {
        return delegate ()
        {
            if (_transformCoroutine != null)
                StopCoroutine(_transformCoroutine);
            _transformCoroutine = StartCoroutine(RotateModel(i, true));
            if (_moduleSolved || _isTransitioning)
                return false;
            Audio.PlaySoundAtTransform("Spin", transform);
            _currentPuzzle = _stage < 3 ? _currentPuzzle.RotateBig(i) : _currentPuzzle.RotateSmall(i);
            UpdateVisuals(_currentPuzzle);
            CheckValidity();
            return false;
        };
    }

    private Action ShiftRelease(int i)
    {
        return delegate ()
        {
            if (_transformCoroutine != null)
                StopCoroutine(_transformCoroutine);
            _transformCoroutine = StartCoroutine(ShiftModel(i, false));
        };
    }

    private Action RotateRelease(int i)
    {
        return delegate ()
        {
            if (_transformCoroutine != null)
                StopCoroutine(_transformCoroutine);
            _transformCoroutine = StartCoroutine(RotateModel(i, false));
        };
    }

    private IEnumerator PrepareStage(RubiksSlidePuzzle puzzle)
    {
        _soundPlaying = true;
        Audio.PlaySoundAtTransform("Start", transform);
        var puzzleToLoad = puzzle.Cells.Where(i => i != 0).ToArray();
        var waitTime = 0.3f / puzzleToLoad.Length;
        for (int i = 0; i < 9; i++)
            ScreenRenderers[i].sharedMaterial = PuzzleMats[0];
        for (int i = 0; i < 9; i++)
        {
            if (puzzle[i] != 0)
            {
                ScreenRenderers[i].sharedMaterial = PuzzleMats[puzzle[i]];
                yield return new WaitForSeconds(waitTime);
            }
        }
        _isTransitioning = false;
        yield return new WaitForSeconds(1.0f);
        _soundPlaying = false;
    }

    private void CheckValidity()
    {
        if (_currentPuzzle.Equals(_solutionPuzzle))
        {
            _isTransitioning = true;
            _stage++;
            StartCoroutine(SetUpPuzzle());
        }
    }

    private void UpdateVisuals(RubiksSlidePuzzle puzzle)
    {
        for (int i = 0; i < 9; i++)
            ScreenRenderers[i].sharedMaterial = PuzzleMats[puzzle[i]];
    }

    private void GeneratePuzzle(int stage)
    {
        var arr = new int[9];
        var nums = Enumerable.Range(0, 9).ToArray().Shuffle();
        var colors = new int[] { 1, 2 }.Shuffle();
        if (stage == 0)
        {
            arr[nums[0]] = colors[0];
        }
        if (stage == 1)
        {
            arr[nums[0]] = colors[0];
            arr[nums[1]] = colors[0];
        }
        if (stage == 2)
        {
            arr[nums[0]] = colors[0];
            arr[nums[1]] = colors[1];
        }
        if (stage == 3)
        {
            arr[nums[0]] = colors[0];
            arr[nums[1]] = colors[0];
            arr[nums[2]] = colors[0];
        }
        if (stage == 4)
        {
            arr[nums[0]] = colors[0];
            arr[nums[1]] = colors[1];
            arr[nums[2]] = colors[1];
        }
        if (stage == 5)
        {
            arr[nums[0]] = colors[0];
            arr[nums[1]] = colors[0];
            arr[nums[2]] = colors[0];
            arr[nums[3]] = colors[0];
        }
        if (stage == 6)
        {
            arr[nums[0]] = colors[0];
            arr[nums[1]] = colors[0];
            arr[nums[2]] = colors[0];
            arr[nums[3]] = colors[0];
            arr[nums[4]] = colors[0];
        }
        if (stage == 7)
        {
            arr[nums[0]] = colors[0];
            arr[nums[1]] = colors[0];
            arr[nums[2]] = colors[1];
            arr[nums[3]] = colors[1];
        }
        if (stage == 8)
        {
            arr[nums[0]] = colors[0];
            arr[nums[1]] = colors[0];
            arr[nums[2]] = colors[1];
            arr[nums[3]] = colors[1];
            arr[nums[4]] = colors[1];
        }
        _solutionPuzzle = new RubiksSlidePuzzle(arr);
        _currentPuzzle = _solutionPuzzle.ShufflePuzzle(_stage);
        StartCoroutine(PrepareStage(_currentPuzzle));
    }

    private IEnumerator SetUpPuzzle()
    {
        Audio.PlaySoundAtTransform("Stage_Clear", transform);
        for (int i = 0; i < 20; i++)
            TimerLights[i].material = TimerMats[1];
        yield return new WaitForSeconds(1.6f);
        if (_stage == 9)
        {
            StartCoroutine(SolveAnimation());
            yield break;
        }
        GeneratePuzzle(_stage);
        for (int i = 0; i < 20; i++)
            TimerLights[i].material = TimerMats[0];
    }

    private IEnumerator ShiftModel(int dir, bool pressIn)
    {
        ModelObj.transform.localEulerAngles = new Vector3(0, 0, 0);
        var duration = 0.1f;
        var elapsed = 0f;
        var goal = new Vector3(dir == 1 ? 0.02f : dir == 3 ? -0.02f : 0, 0, dir == 0 ? 0.02f : dir == 2 ? -0.02f : 0);
        var curPos = ModelObj.transform.localPosition;
        while (elapsed < duration)
        {
            ModelObj.transform.localPosition = new Vector3(
                Easing.InOutQuad(elapsed, curPos.x, pressIn ? goal.x : 0f, duration),
                0f,
                Easing.InOutQuad(elapsed, curPos.z, pressIn ? goal.z : 0f, duration));
            yield return null;
            elapsed += Time.deltaTime;
        }
        ModelObj.transform.localPosition = new Vector3(pressIn ? goal.x : 0f, 0f, pressIn ? goal.z : 0f);
    }

    private IEnumerator RotateModel(int dir, bool pressIn)
    {
        ModelObj.transform.localPosition = new Vector3(0, 0, 0);
        var duration = 0.1f;
        var elapsed = 0f;
        var goal = dir == 0 ? 10f : -10f;
        var curPos = ModelObj.transform.localEulerAngles.y;
        if (curPos > 20)
            curPos -= 360f;
        while (elapsed < duration)
        {
            ModelObj.transform.localEulerAngles = new Vector3(
                0f,
                Easing.InOutQuad(elapsed, curPos, pressIn ? goal : 0f, duration),
                0f);
            yield return null;
            elapsed += Time.deltaTime;
        }
        ModelObj.transform.localEulerAngles = new Vector3(0f, pressIn ? goal : 0f, 0f);
    }

    private IEnumerator SolveAnimation()
    {
        Audio.PlaySoundAtTransform("Solve", transform);
        UpdateVisuals(new RubiksSlidePuzzle(new int[9]));
        yield return new WaitForSeconds(0.45f);
        for (int i = 0; i < 20; i++)
            TimerLights[i].sharedMaterial = TimerMats[0];
        var p = new RubiksSlidePuzzle(Enumerable.Range(0, 9).Select(i => i % 3).ToArray().Shuffle());
        var waitTimes = new float[] { 0.05f, 0.05f, 0.05f, 0.05f, 0.05f, 0.05f, 0.05f, 0.05f, 0.05f, 0.05f, 0.075f, 0.1f, 0.125f, 0.15f, 0.175f, 0.2f, 0.225f };
        for (int i = 0; i < waitTimes.Length; i++)
        {
            var a = p.ShufflePuzzle(3);
            UpdateVisuals(a);
            yield return new WaitForSeconds(waitTimes[i]);
        }
        for (int i = 0; i < 9; i++)
            ScreenRenderers[i].sharedMaterial = PuzzleMats[1];
        yield return new WaitForSeconds(0.1f);
        for (int i = 0; i < 9; i++)
            ScreenRenderers[i].sharedMaterial = PuzzleMats[0];
        yield return new WaitForSeconds(0.1f);
        for (int i = 0; i < 9; i++)
            ScreenRenderers[i].sharedMaterial = PuzzleMats[2];
        yield return new WaitForSeconds(0.15f);
        for (int i = 0; i < 9; i++)
            ScreenRenderers[i].sharedMaterial = PuzzleMats[0];
        yield return new WaitForSeconds(0.2f);
        for (int i = 0; i < 9; i++)
            ScreenRenderers[i].sharedMaterial = PuzzleMats[2];
        yield return new WaitForSeconds(0.25f);
        for (int i = 0; i < 9; i++)
            ScreenRenderers[i].sharedMaterial = PuzzleMats[0];
        yield return new WaitForSeconds(0.3f);
        for (int i = 0; i < 9; i++)
            ScreenRenderers[i].sharedMaterial = PuzzleMats[3];
        for (int i = 0; i < 20; i++)
            TimerLights[i].sharedMaterial = TimerMats[2];
        Module.HandlePass();
        _moduleSolved = true;
    }

#pragma warning disable 0414
    private readonly string TwithchHelpMessage = "!{0} up right down left clockwise counterclockwise [Shift or rotate the grid.] | !{0} center [View the solution puzzle.] | Movements can be abbreviated to u, r, d, l, cw, ccw.";
#pragma warning restore 0414

    private IEnumerator ProcessTwitchCommand(string command)
    {
        command = command.Trim().ToLowerInvariant();
        var m = Regex.Match(command, @"^\s*center\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (m.Success)
        {
            yield return null;
            CenterSel.OnInteract();
            yield return new WaitForSeconds(4f);
            CenterSel.OnInteractEnded();
            yield break;
        }
        var parameters = command.Split(' ');
        var list = new List<KMSelectable>();
        for (int i = 0; i < parameters.Length; i++)
        {
            switch (parameters[i])
            {
                case "up":
                case "u":
                case "north":
                case "n":
                    list.Add(ShiftSels[0]);
                    break;
                case "right":
                case "r":
                case "east":
                case "e":
                    list.Add(ShiftSels[1]);
                    break;
                case "down":
                case "d":
                case "south":
                case "s":
                    list.Add(ShiftSels[2]);
                    break;
                case "left":
                case "l":
                case "west":
                case "w":
                    list.Add(ShiftSels[3]);
                    break;
                case "clockwise":
                case "cw":
                    list.Add(RotateSels[0]);
                    break;
                case "counterclockwise":
                case "ccw":
                    list.Add(RotateSels[4]);
                    break;
                default:
                    yield break;
            }
        }
        yield return null;
        yield return "solve";
        for (int i = 0; i < list.Count; i++)
        {
            list[i].OnInteract();
            yield return new WaitForSeconds(0.1f);
            list[i].OnInteractEnded();
            yield return new WaitForSeconds(0.1f);
        }
    }

    struct QueueItem
    {
        public RubiksSlidePuzzle Puzzle { get; private set; }
        public RubiksSlidePuzzle Parent { get; private set; }
        public int Action { get; private set; }
        public QueueItem(RubiksSlidePuzzle puzzle, RubiksSlidePuzzle parent, int action)
        {
            Puzzle = puzzle;
            Parent = parent;
            Action = action;
        }
    }

    private IEnumerator TwitchHandleForcedSolve()
    {
        while (!_moduleSolved)
        {
            while (_isTransitioning)
                yield return true;
            var visited = new Dictionary<RubiksSlidePuzzle, QueueItem>();
            var q = new Queue<QueueItem>();
            q.Enqueue(new QueueItem(_currentPuzzle, null, 0));
            while (q.Count > 0)
            {
                var qi = q.Dequeue();
                if (visited.ContainsKey(qi.Puzzle))
                    continue;
                visited[qi.Puzzle] = qi;
                if (qi.Puzzle.Equals(_solutionPuzzle))
                    break;
                q.Enqueue(new QueueItem(qi.Puzzle.Shift(0), qi.Puzzle, 0));
                q.Enqueue(new QueueItem(qi.Puzzle.Shift(1), qi.Puzzle, 1));
                q.Enqueue(new QueueItem(qi.Puzzle.Shift(2), qi.Puzzle, 2));
                q.Enqueue(new QueueItem(qi.Puzzle.Shift(3), qi.Puzzle, 3));
                if (_stage < 3)
                {
                    q.Enqueue(new QueueItem(qi.Puzzle.RotateBig(0), qi.Puzzle, 4));
                    q.Enqueue(new QueueItem(qi.Puzzle.RotateBig(1), qi.Puzzle, 5));
                }
                else
                {
                    q.Enqueue(new QueueItem(qi.Puzzle.RotateSmall(0), qi.Puzzle, 4));
                    q.Enqueue(new QueueItem(qi.Puzzle.RotateSmall(1), qi.Puzzle, 5));
                }
            }
            var r = _solutionPuzzle;
            var path = new List<int>();
            while (true)
            {
                var nr = visited[r];
                if (nr.Parent == null)
                    break;
                path.Add(nr.Action);
                r = nr.Parent;
            }
            yield return new WaitForSeconds(0.1f);
            for (int i = path.Count - 1; i >= 0; i--)
            {
                if (path[i] < 4)
                {
                    ShiftSels[path[i]].OnInteract();
                    yield return new WaitForSeconds(0.1f);
                    ShiftSels[path[i]].OnInteractEnded();
                    yield return new WaitForSeconds(0.1f);
                }
                else
                {
                    RotateSels[path[i] - 1].OnInteract();
                    yield return new WaitForSeconds(0.1f);
                    RotateSels[path[i] - 1].OnInteractEnded();
                    yield return new WaitForSeconds(0.1f);
                }
            }
            yield return null;
            while (_isTransitioning || _soundPlaying)
                yield return true;
        }
    }
}