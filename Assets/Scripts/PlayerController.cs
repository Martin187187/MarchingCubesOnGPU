using UnityEngine;

public class PlayerController : MonoBehaviour
{
    public float moveSpeed = 5f; // Movement speed
    public float rotationSpeed = 700f; // Rotation speed
    public float gravity = 9.81f; // Gravity force
    public float jumpHeight = 2f; // Jump strength

    private CharacterController controller;
    private Vector3 velocity; // Stores vertical movement (gravity + jump)
    private bool isGrounded;

    void Start()
    {
        controller = GetComponent<CharacterController>();
    }

    void Update()
    {
        HandleMovement();
        HandleRotation();
        HandleJump();
    }

    private void HandleMovement()
    {
        isGrounded = controller.isGrounded; // Check if on ground

        float moveX = Input.GetAxis("Horizontal");
        float moveZ = Input.GetAxis("Vertical");

        Vector3 moveDirection = transform.right * moveX + transform.forward * moveZ;
        if (moveDirection.magnitude > 1)
            moveDirection.Normalize(); // Normalize diagonal movement speed

        Vector3 movement = moveDirection * moveSpeed * Time.deltaTime;

        // Apply gravity if not grounded
        if (isGrounded && velocity.y < 0)
        {
            velocity.y = -2f; // Small negative value to keep grounded
        }
        else
        {
            velocity.y -= gravity * Time.deltaTime;
        }

        // Apply movement and gravity
        controller.Move(movement + velocity * Time.deltaTime);
    }

    private void HandleJump()
    {
        if (isGrounded && Input.GetButtonDown("Jump"))
        {
            velocity.y = Mathf.Sqrt(jumpHeight * 2f * gravity); // Jump formula
        }
    }

    private void HandleRotation()
    {
        float mouseX = Input.GetAxis("Mouse X") * rotationSpeed ;
        transform.Rotate(Vector3.up * mouseX);
    }
}