module mfnet/relay

go 1.24

require golang.org/x/crypto v0.0.0

require golang.org/x/sys v0.0.0 // indirect

// copies of the Go project's own crypto packages (BSD licence, see third_party/*/LICENSE)
replace golang.org/x/crypto => ./third_party/crypto

replace golang.org/x/sys => ./third_party/sys
