using System.Collections;
using System.Collections.Generic;
using Unity.VisualScripting;
using UnityEngine;
using static TMPro.SpriteAssetUtilities.TexturePacker_JsonArray;

public class JumpBehaviour : MonoBehaviour
{
    //Component to be added to player character. Allows customising jump behaviour by frame.
    //Makes use of the Transform method, so includes own bound checking. Requires level geometry be tagged in certain ways (see below)
    //Made by Gale Verney - https://github.com/NullAux

    //Current issues / future extensions
    // 1-Bounds checking is tightly enforced for floor (player is teleported to correct height on impact) but not walls/ceiling (where they can clip a bit in, and are then prevented moving further)
    // This leads to unintuitive scenarios (player gets caughts on lower block corner, stops entirely; player clips into edge of floor, gets teleported to top)
    // Can extend tight bound enforcement to others, and possibly add a check that player centre is => floor level before teleporting them up.
    // 2-Velocity can be flipped in the air. See comments around DirectionOptions on how to change this
    // 3-Excessive use of tags and particular tagging. It may be possible to simply determine what plane is being touched on collision,
    // based on player/surface positions.
    // 4-Excessive bools/conditionals to pass information between asynchronous methods (update, fixedUpdate, collisionEnter/Exit)
    // It may be possible to dodge this with some cleverly designed methods that intersect all 3, perhaps an overarching loop
    // That establishes which code to be run in advance.

    //Guide to tagging
    //Current implementation uses tags to determine if the collider it is touching is a floor, wall etc.
    //4 tags are used:
    //  -Floor (player stands on, ie the top of an object)
    //  -Ceiling (ie the bottom of an object)
    //  -RWall (a wall to the player's right, which blocks them from going right, ie the *left* side of an object)
    //  -LWall (a wall to the player's left, which blocks them from going left, ie the *right* side of an object)
    // Any plane should have a rigidbody2D (set to static) and a boxCollider2D, and appropriate tag
    // For parts of the level met in one direction, just one gameobject with one tag suffices (eg a sheer wall to the player's left on level start - one tall square tagged LWall).
    // An object which can be interacted with in multiple directions should be made of one gameobject (with spriterenderer) and multiple children (each one plane, with appropriate tag).
    // These children and their colliders can overlap, but make sure the edges of colliders don't overlap, otherwise unexpected behaviour will be more frequent
    // (eg touching a wall near it's bottom will act like you have a ceiling above you).
    // Colliders should have some thickness, since player can slightly clip through (see above on tight bounds checking)
    //
    //  ---Floor---
    // |x x x x x x|
    // R x x x x x L
    // W x x x x x W
    // a x x x x x a
    // l x x x x x l
    // l x x x x x l
    // | x x x x x |
    //  --Ceiling--

    //Guide to frames / jump windows
    //When the player jumps, it begins a sequence of jump windows which give the velocity for each frame.
    //These run on FixedUpdates (50 times a second by default).
    //Jump behaviour is managed in the editor
    //1. Each element in FrameData is a window, ie a period of frames where the player has the same velocity applied every frame.
    //1a. 'Window Frames' is how many frames this window lasts (how long this velocity is maintained).
    //1b. Velocity (x and y) determines how far the player will move each frame for this window.
    //2. After the last frame of the window, the next window begins. This can be used to create a smooth arc
    // (by decreasing the y gain each window).
    // Negative y values can also be used, resulting in falling. Negative x will push the player away from the direction they're facing.
    //3. After the last window, Falling Velocity will be used each frame until landing. This is the player's 'terminal velocity'.

    //Private variable list

    //Frame info. Used by jump. Add frame windows in editor.
    int currentFrame = 0;
    int currentFrameWindow = 0;
    int numOfFrameWindows;//Set at initialization

    //Relating to movement. Used in FixedUpdate before being applied in one Transform call.
    //In the current implementation, player can change direction mid air, quickly flipping x velocity.
    //This could be changed by creating another DirectionOptions which locks in direction at the start of each jump,
    //which Jump() would then use instead of playerDirection.
    enum DirectionOptions
    {
        LEFT = -1,
        RIGHT = 1
    }
    DirectionOptions playerDirection;
    Vector2 frameMovementVector;

    //Caps used for collision detection
    //Currently high velocity allows potential of clipping through a wall.
    //In future could save last in bounds value to lock the player to.
    bool LeftXCapped = false;
    bool RightXCapped = false;
    bool heightCapped = false;

    //State of player - changes are found in Update (input) and OnCollisionEnter/Exit (Collisions) and implemented in FixedUpdate
    //'IsJumping' and 'canJump' can be amalgemated, but keeping them seperate allows for possibility of double jump / similar
    bool isJumping = false;
    bool isFalling = false;
    bool isWalking = false;
    bool canJump = false;

    //Serialized variables
    //Available in editor to customise jump behaviour
    [System.Serializable]
    public class JumpWindow
    {
        public int windowFrames;//the number of frames this window runs
        public Vector2 Velocity;
    }
    public JumpWindow[] FrameData;
    public Vector2 FallingVelocity;//This is linear fall speed ie terminal velocity. Add negative y velocity frames for a smoother transition.
    public float walkSpeed = 0;

    //Used for correcting clipping
    float playerHeight;

    // Start is called before the first frame update
    void Start()
    {
        numOfFrameWindows = FrameData.Length;
        isFalling = true;
        playerHeight = (gameObject.GetComponent<BoxCollider2D>().size.y * gameObject.transform.lossyScale.y);
    }

    // Update is called once per frame
    //Checks for buttons are done in update, so that they can't be missed in between FixedUpdates
    void Update()
    {
        //Check for jump
        if (Input.GetButtonDown("Jump") && canJump)
        { isJumping = true;
            canJump = false;
        }

        //Check for walking
        if (Input.GetKey(KeyCode.A))
        {
            isWalking = true;
            playerDirection = DirectionOptions.LEFT;
        }

        else if (Input.GetKey(KeyCode.D))
        {
            isWalking = true;
            playerDirection = DirectionOptions.RIGHT;
        }

        else
        {
            isWalking = false;
        }

    }

    //FxiedUpdate handles movement for each frame
    private void FixedUpdate()
    {
        frameMovementVector = Vector2.zero;

        if (isJumping)
        {
            frameMovementVector += Jump();
        }

        else if (isFalling)
        {
            frameMovementVector -= FallingVelocity;
        }
        
        if (isWalking)
        {
            frameMovementVector += Walk((int)playerDirection);
        }

        frameMovementVector = CollisionDetection(frameMovementVector);
        gameObject.transform.Translate(frameMovementVector);
    }

    //Methods
    Vector2 CollisionDetection(Vector2 rawMovementVector)
    {
        if (heightCapped && rawMovementVector.y > 0)
        {
            rawMovementVector.y = 0;
        }

        if (LeftXCapped && rawMovementVector.x < 0)
        { rawMovementVector.x = 0; }

        else if (RightXCapped && rawMovementVector.x > 0)
        {
            rawMovementVector.x = 0;
        }

        return rawMovementVector;
    }
    Vector2 Jump()
    {

        //Update current frame window
        currentFrame++;
        if (currentFrame > FrameData[currentFrameWindow].windowFrames)
        { currentFrameWindow++;
            currentFrame = 0;
        }
        //Check for the end of jump sequence
        if(currentFrameWindow == numOfFrameWindows)
        {
            currentFrame = 0;
            currentFrameWindow = 0;
            isJumping = false;
            isFalling = true;
        }

        Vector2 jumpVelocity = new Vector2(FrameData[currentFrameWindow].Velocity.x * (int)playerDirection, FrameData[currentFrameWindow].Velocity.y);
        return jumpVelocity;
    }

    Vector2 Walk(int direction)
    {
        Vector2 walkingVector = new Vector2(walkSpeed * direction, 0);
        return walkingVector;
    }

    //Collisions
    private void OnCollisionEnter2D(Collision2D collision)
    {
        if (collision.gameObject.CompareTag("Floor"))
        {
            isFalling = false;
            isJumping = false;
            canJump = true;
            currentFrame = 0;
            currentFrameWindow = 0;

            //Move the player character back to floor level, since Transform() clips them in somewhat
            BoxCollider2D collisionBox = collision.gameObject.GetComponent<BoxCollider2D>();
            float collisionLossyScaleY = collision.gameObject.transform.lossyScale.y;

            float colliderHeight = collisionBox.size.y * collisionLossyScaleY;
            float correctDistance = (playerHeight + colliderHeight) / 2;
            float colliderPosition = collision.gameObject.transform.position.y + (collisionLossyScaleY * collisionBox.offset.y);
            float playerPosition = gameObject.transform.position.y;
            float currentDistance = playerPosition - colliderPosition;
            float distanceCorrection = correctDistance - currentDistance;
            gameObject.transform.Translate(new Vector2(0, distanceCorrection));

        }

        else if (collision.gameObject.CompareTag("Ceiling"))
        {
            heightCapped = true;
        }

        else if (collision.gameObject.CompareTag("RWall"))
        {
            RightXCapped = true;
        }

        else if (collision.gameObject.CompareTag("LWall"))
        {
            LeftXCapped = true;
        }

    }

    private void OnCollisionExit2D(Collision2D collision)
    {
        if (collision.gameObject.CompareTag("Floor"))
        {
            canJump = false;//Change to 'jumps--' if air jumps are allowed
            if (!isJumping) { isFalling = true; }

        }

        else if (collision.gameObject.CompareTag("Ceiling"))
        {
            heightCapped = false;
        }

        else if (collision.gameObject.CompareTag("RWall"))
        {
            RightXCapped = false;
        }

        else if (collision.gameObject.CompareTag("LWall"))
        {
            LeftXCapped = false;
        }
    }


}
